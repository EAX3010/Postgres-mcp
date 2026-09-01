using ModelContextProtocol.Server;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;
using System.ComponentModel;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public class ExecuteTools
{
    private readonly IPostgresService _postgres;
    private readonly IConnectionManager _connectionManager;
    private readonly ISafetyGuard _safetyGuard;
    private readonly IAuditLogger _auditLogger;

    public ExecuteTools(
        IPostgresService postgres,
        IConnectionManager connectionManager,
        ISafetyGuard safetyGuard,
        IAuditLogger auditLogger)
    {
        _postgres = postgres;
        _connectionManager = connectionManager;
        _safetyGuard = safetyGuard;
        _auditLogger = auditLogger;
    }

    /// <summary>Records attempts that never reached the database, which are the most interesting audit events.</summary>
    private async Task LogAttemptAsync(string database, string sql, SafetyCheckResult check, bool dryRun, bool rejected, string? reason)
    {
        try
        {
            await _auditLogger.LogAsync(new AuditEntry
            {
                Database = database,
                Query = SqlText.RedactSecrets(sql),
                Operation = check.OperationType,
                RiskLevel = check.RiskLevel.ToWireString(),
                DryRun = dryRun,
                Rejected = rejected,
                Success = false,
                ErrorMessage = reason
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AUDIT-FAILURE] {ex.Message}");
        }
    }

    [McpServerTool, Description("Execute a single INSERT, UPDATE, DELETE or DDL statement with safety checks")]
    public async Task<string> Execute(
        [Description("Name of the database connection to use")] string database,
        [Description("A single SQL statement to execute")] string sql,
        [Description("Set to true to preview the operation without executing")] bool dryRun = false,
        [Description("Confirm execution of a risky operation")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";

        var check = _safetyGuard.CheckQuery(sql);

        if (check.IsRejected)
        {
            await LogAttemptAsync(database, sql, check, dryRun, rejected: true, check.RejectionReason);
            return $"Error: {check.RejectionReason}";
        }

        if (check.IsReadOnly)
            return "Error: Use the 'query' tool for read-only statements.";

        if (dryRun)
        {
            if (!_safetyGuard.Settings.EnableDryRun)
                return "Error: Dry-run previews are disabled by configuration (Safety.EnableDryRun=false).";

            await LogAttemptAsync(database, sql, check, dryRun: true, rejected: false, null);
            return _safetyGuard.GetDryRunMessage(sql, database);
        }

        if (check.RequiresConfirmation && !confirm)
        {
            await LogAttemptAsync(database, sql, check, dryRun: false, rejected: true, "awaiting confirmation");
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                riskLevel = check.RiskLevel.ToWireString(),
                operation = check.OperationType,
                warnings = check.Warnings,
                message = "This operation requires confirmation. Set confirm=true to proceed.",
                query = SqlText.RedactSecrets(sql)
            });
        }

        try
        {
            var audit = new AuditContext(check.OperationType, check.RiskLevel.ToWireString(), DryRun: false, Confirmed: confirm);
            var result = await _postgres.ExecuteNonQueryAsync(database, sql, audit, ct);

            if (!result.Success)
                return $"Error: {result.ErrorMessage}";

            return ToolJson.Serialize(new
            {
                success = true,
                rowsAffected = result.RowsAffected,
                operation = check.OperationType,
                riskLevel = check.RiskLevel.ToWireString(),
                executionTime = $"{result.ExecutionTime.TotalMilliseconds:F2}ms"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Execute several SQL statements inside a single transaction. All succeed or all roll back.")]
    public async Task<string> ExecuteBatch(
        [Description("Name of the database connection to use")] string database,
        [Description("SQL statements to execute, one statement per array element")] string[] statements,
        [Description("Set to true to preview the operations without executing")] bool dryRun = false,
        [Description("Confirm execution of risky operations")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";

        if (statements is null || statements.Length == 0)
            return "Error: At least one statement is required.";

        var checks = statements.Select(s => _safetyGuard.CheckQuery(s)).ToList();

        // Each element must be exactly one statement; otherwise a single element could smuggle
        // extra statements past the per-statement risk assessment below.
        var rejected = checks
            .Select((c, i) => (Check: c, Index: i))
            .Where(x => x.Check.IsRejected)
            .ToList();

        if (rejected.Count > 0)
        {
            return ToolJson.Serialize(new
            {
                error = "One or more statements were rejected.",
                rejected = rejected.Select(x => new
                {
                    index = x.Index,
                    reason = x.Check.RejectionReason,
                    sql = SqlText.RedactSecrets(statements[x.Index])
                })
            });
        }

        var highest = checks.Count == 0 ? RiskLevel.Low : checks.Max(c => c.RiskLevel);
        var needsConfirmation = checks.Any(c => c.RequiresConfirmation);

        if (dryRun)
        {
            if (!_safetyGuard.Settings.EnableDryRun)
                return "Error: Dry-run previews are disabled by configuration (Safety.EnableDryRun=false).";

            return ToolJson.Serialize(new
            {
                dryRun = true,
                database,
                statementCount = statements.Length,
                highestRiskLevel = highest.ToWireString(),
                requiresConfirmation = needsConfirmation,
                statements = statements.Select((s, i) => new
                {
                    index = i,
                    sql = SqlText.RedactSecrets(s),
                    operation = checks[i].OperationType,
                    riskLevel = checks[i].RiskLevel.ToWireString(),
                    warnings = checks[i].Warnings
                })
            });
        }

        if (needsConfirmation && !confirm)
        {
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                highestRiskLevel = highest.ToWireString(),
                riskyStatements = checks
                    .Select((c, i) => (Check: c, Index: i))
                    .Where(x => x.Check.RequiresConfirmation)
                    .Select(x => new
                    {
                        index = x.Index,
                        sql = SqlText.RedactSecrets(statements[x.Index]),
                        operation = x.Check.OperationType,
                        riskLevel = x.Check.RiskLevel.ToWireString(),
                        warnings = x.Check.Warnings
                    }),
                message = "This batch contains risky operations. Set confirm=true to proceed."
            });
        }

        try
        {
            var result = await _postgres.ExecuteBatchAsync(database, statements, ct);

            if (!result.Success)
            {
                return ToolJson.Serialize(new
                {
                    success = false,
                    rolledBack = result.RolledBack,
                    failedStatementIndex = result.FailedStatementIndex,
                    error = result.ErrorMessage,
                    message = "The transaction was rolled back; no statement in this batch took effect."
                });
            }

            return ToolJson.Serialize(new
            {
                success = true,
                statementsExecuted = result.Statements.Count,
                totalRowsAffected = result.Statements.Sum(s => s.RowsAffected),
                statements = result.Statements.Select(s => new { s.Index, s.RowsAffected }),
                executionTime = $"{result.ExecutionTime.TotalMilliseconds:F2}ms"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
