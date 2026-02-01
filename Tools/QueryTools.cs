using ModelContextProtocol.Server;
using PostgresMcpServer.Services;
using System.ComponentModel;
using System.Text.Json;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public class QueryTools
{
    private readonly IPostgresService _postgres;
    private readonly IConnectionManager _connectionManager;
    private readonly ISafetyGuard _safetyGuard;

    public QueryTools(IPostgresService postgres, IConnectionManager connectionManager, ISafetyGuard safetyGuard)
    {
        _postgres = postgres;
        _connectionManager = connectionManager;
        _safetyGuard = safetyGuard;
    }

    [McpServerTool, Description("Execute a SELECT query on the specified database. Returns query results as JSON.")]
    public async Task<string> Query(
        [Description("Name of the database connection to use")] string database,
        [Description("SQL SELECT query to execute")] string sql,
        [Description("Maximum number of rows to return (default: 100)")] int limit = 100,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";
        }

        var safetyCheck = _safetyGuard.CheckQuery(sql);
        if (safetyCheck.OperationType != "SELECT" && safetyCheck.OperationType != "EXPLAIN")
        {
            return $"Error: This tool only supports SELECT queries. Use 'execute' for {safetyCheck.OperationType} operations.";
        }

        // Add LIMIT if not present
        if (!sql.ToUpperInvariant().Contains("LIMIT"))
        {
            sql = $"{sql.TrimEnd().TrimEnd(';')} LIMIT {limit}";
        }

        var result = await _postgres.ExecuteQueryAsync(database, sql, ct);

        if (!result.Success)
        {
            return $"Error: {result.ErrorMessage}";
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            rowCount = result.Rows.Count,
            columns = result.Columns,
            rows = result.Rows,
            executionTime = result.ExecutionTime.TotalMilliseconds + "ms"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("List all configured database connections")]
    public string ListDatabases()
    {
        var databases = _connectionManager.GetDatabaseNames().ToList();
        return JsonSerializer.Serialize(new
        {
            databases,
            count = databases.Count
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get the execution plan for a query without running it")]
    public async Task<string> Explain(
        [Description("Name of the database connection to use")] string database,
        [Description("SQL query to analyze")] string sql,
        [Description("Run EXPLAIN ANALYZE to get actual execution statistics (runs the query)")] bool analyze = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";
        }

        try
        {
            var plan = await _postgres.GetExplainPlanAsync(database, sql, analyze, ct);
            return $"Query Plan{(analyze ? " (with execution statistics)" : "")}:\n\n{plan}";
        }
        catch (Exception ex)
        {
            return $"Error analyzing query: {ex.Message}";
        }
    }
}
