using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;
using System.ComponentModel;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public class QueryTools
{
    private readonly IPostgresService _postgres;
    private readonly IConnectionManager _connectionManager;
    private readonly ISafetyGuard _safetyGuard;
    private readonly LimitSettings _limits;

    public QueryTools(
        IPostgresService postgres,
        IConnectionManager connectionManager,
        ISafetyGuard safetyGuard,
        IOptions<DatabaseConfig> config)
    {
        _postgres = postgres;
        _connectionManager = connectionManager;
        _safetyGuard = safetyGuard;
        _limits = config.Value.Limits;
    }

    /// <summary>Statements that can legally be wrapped in a limiting subquery.</summary>
    private static readonly string[] Wrappable = ["SELECT", "TABLE", "VALUES"];

    [McpServerTool, Description("Execute a read-only SELECT query. Runs inside a READ ONLY transaction that is always rolled back, so it cannot modify data.")]
    public async Task<string> Query(
        [Description("Name of the database connection to use")] string database,
        [Description("A single SQL SELECT statement")] string sql,
        [Description("Maximum number of rows to return (default: 100)")] int limit = 100,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";

        var check = _safetyGuard.CheckQuery(sql);

        if (check.IsRejected)
            return $"Error: {check.RejectionReason}";

        if (!check.IsReadOnly)
            return $"Error: This tool only runs read-only statements. Detected '{check.OperationType}' - " +
                   $"use 'execute' for writes, or 'explain' for a plan.";

        // The caller's limit is advisory; the configured ceiling is not negotiable.
        var effectiveLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, _limits.MaxRows);
        var effectiveSql = WrapWithLimit(sql, check.OperationType, effectiveLimit);

        try
        {
            var result = await _postgres.ExecuteReadOnlyAsync(database, effectiveSql, effectiveLimit, ct);

            if (!result.Success)
                return $"Error: {result.ErrorMessage}";

            return ToolJson.SerializeRows(
                (rows, sizeTruncated) => new
                {
                    success = true,
                    rowCount = rows.Count,
                    truncated = result.Truncated || sizeTruncated,
                    limit = effectiveLimit,
                    columns = result.Columns,
                    rows,
                    executionTime = $"{result.ExecutionTime.TotalMilliseconds:F2}ms"
                },
                result.Rows,
                _limits.MaxResponseBytes);
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

    /// <summary>
    /// Wraps the statement in a limiting subquery instead of appending LIMIT to the text.
    /// Appending was defeated by a trailing comment, and the previous substring test for
    /// "LIMIT" also matched the word inside literals and identifiers. The newline before the
    /// closing paren terminates any trailing line comment.
    /// </summary>
    private static string WrapWithLimit(string sql, string operationType, int limit)
    {
        if (!Wrappable.Contains(operationType)) return sql;

        var trimmed = sql.TrimEnd().TrimEnd(';');
        return $"SELECT * FROM (\n{trimmed}\n) AS _mcp_limited LIMIT {limit}";
    }

    [McpServerTool, Description("List all configured database connections")]
    public string ListDatabases()
    {
        var databases = _connectionManager.GetDatabaseNames().ToList();
        var errors = _connectionManager.ConfigurationErrors;

        return ToolJson.Serialize(new
        {
            databases,
            count = databases.Count,
            misconfigured = errors.Count == 0 ? null : errors
        });
    }

    [McpServerTool, Description("Get the execution plan for a query. With analyze=true the statement really runs, so it is executed inside a transaction that is always rolled back.")]
    public async Task<string> Explain(
        [Description("Name of the database connection to use")] string database,
        [Description("A single SQL statement to analyze")] string sql,
        [Description("Run EXPLAIN ANALYZE for real execution statistics. The statement executes, but inside a rolled-back transaction.")] bool analyze = false,
        [Description("Confirm running EXPLAIN ANALYZE against a writing statement")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";

        var check = _safetyGuard.CheckQuery(sql);

        if (check.IsRejected)
            return $"Error: {check.RejectionReason}";

        // EXPLAIN ANALYZE on a writing statement executes it. The rollback protects the data,
        // but side effects outside the transaction (sequences, triggers doing external work)
        // still happen, so this stays an explicit decision.
        if (analyze && !check.IsReadOnly && !confirm)
        {
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                riskLevel = check.RiskLevel.ToWireString(),
                operation = check.OperationType,
                warnings = check.Warnings,
                message = "EXPLAIN ANALYZE executes this statement. It will run inside a transaction that is " +
                          "rolled back, but sequence advances and other non-transactional side effects persist. " +
                          "Set confirm=true to proceed, or analyze=false for a plan without executing.",
                query = SqlText.RedactSecrets(sql)
            });
        }

        try
        {
            var plan = await _postgres.GetExplainPlanAsync(database, sql, analyze, ct);
            var header = analyze
                ? "Query Plan (with execution statistics; statement was rolled back)"
                : "Query Plan";
            return $"{header}:\n\n{plan}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error analyzing query: {ex.Message}";
        }
    }
}
