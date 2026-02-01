using ModelContextProtocol.Server;
using PostgresMcpServer.Services;
using System.ComponentModel;
using System.Text.Json;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public class ExecuteTools
{
    private readonly IPostgresService _postgres;
    private readonly IConnectionManager _connectionManager;
    private readonly ISafetyGuard _safetyGuard;

    public ExecuteTools(IPostgresService postgres, IConnectionManager connectionManager, ISafetyGuard safetyGuard)
    {
        _postgres = postgres;
        _connectionManager = connectionManager;
        _safetyGuard = safetyGuard;
    }

    [McpServerTool, Description("Execute an INSERT, UPDATE, or DELETE statement on the specified database")]
    public async Task<string> Execute(
        [Description("Name of the database connection to use")] string database,
        [Description("SQL statement to execute (INSERT, UPDATE, DELETE)")] string sql,
        [Description("Set to true to preview the operation without executing")] bool dryRun = false,
        [Description("Confirm execution of critical operations")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";
        }

        var safetyCheck = _safetyGuard.CheckQuery(sql);

        // Block SELECT queries - use query tool instead
        if (safetyCheck.OperationType == "SELECT")
        {
            return "Error: Use the 'query' tool for SELECT statements.";
        }

        // Dry run mode
        if (dryRun)
        {
            return _safetyGuard.GetDryRunMessage(sql, database);
        }

        // Check for critical operations requiring confirmation
        if (safetyCheck.RequiresConfirmation && !confirm)
        {
            return JsonSerializer.Serialize(new
            {
                requiresConfirmation = true,
                riskLevel = safetyCheck.RiskLevel,
                operation = safetyCheck.OperationType,
                warnings = safetyCheck.Warnings,
                message = "This operation requires confirmation. Set confirm=true to proceed.",
                query = sql
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _postgres.ExecuteNonQueryAsync(database, sql, ct);

        if (!result.Success)
        {
            return $"Error: {result.ErrorMessage}";
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            rowsAffected = result.RowsAffected,
            operation = safetyCheck.OperationType,
            executionTime = result.ExecutionTime.TotalMilliseconds + "ms"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Execute a batch of SQL statements in a transaction")]
    public async Task<string> ExecuteBatch(
        [Description("Name of the database connection to use")] string database,
        [Description("Array of SQL statements to execute")] string[] statements,
        [Description("Set to true to preview the operations without executing")] bool dryRun = false,
        [Description("Confirm execution of critical operations")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";
        }

        // Check all statements for safety
        var criticalOps = new List<object>();
        foreach (var stmt in statements)
        {
            var check = _safetyGuard.CheckQuery(stmt);
            if (check.IsCritical)
            {
                criticalOps.Add(new { statement = stmt, warnings = check.Warnings, riskLevel = check.RiskLevel });
            }
        }

        if (dryRun)
        {
            return JsonSerializer.Serialize(new
            {
                dryRun = true,
                database,
                statementCount = statements.Length,
                statements = statements.Select((s, i) => new { index = i, sql = s, safety = _safetyGuard.CheckQuery(s) })
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        if (criticalOps.Count > 0 && !confirm)
        {
            return JsonSerializer.Serialize(new
            {
                requiresConfirmation = true,
                criticalOperations = criticalOps,
                message = "Batch contains critical operations. Set confirm=true to proceed."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        // Execute in transaction
        var transactionSql = $"""
            BEGIN;
            {string.Join(";\n", statements)};
            COMMIT;
            """;

        var result = await _postgres.ExecuteNonQueryAsync(database, transactionSql, ct);

        if (!result.Success)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = result.ErrorMessage,
                message = "Transaction rolled back due to error"
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            statementsExecuted = statements.Length,
            executionTime = result.ExecutionTime.TotalMilliseconds + "ms"
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
