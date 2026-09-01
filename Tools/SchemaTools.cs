using ModelContextProtocol.Server;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;
using System.ComponentModel;
using System.Text;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public class SchemaTools
{
    private readonly IPostgresService _postgres;
    private readonly IConnectionManager _connectionManager;

    public SchemaTools(IPostgresService postgres, IConnectionManager connectionManager)
    {
        _postgres = postgres;
        _connectionManager = connectionManager;
    }

    [McpServerTool, Description("List all schemas in the database")]
    public async Task<string> ListSchemas(
        [Description("Name of the database connection to use")] string database,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";

        try
        {
            var schemas = await _postgres.GetSchemasAsync(database, ct);
            return ToolJson.Serialize(new
            {
                database,
                schemas = schemas.Select(s => s.SchemaName),
                count = schemas.Count
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool, Description("List all tables in the database or a specific schema")]
    public async Task<string> ListTables(
        [Description("Name of the database connection to use")] string database,
        [Description("Optional: filter by schema name")] string? schema = null,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";

        try
        {
            var tables = await _postgres.GetAllTablesAsync(database, schema, ct);
            return ToolJson.Serialize(new
            {
                database,
                schema = schema ?? "all",
                tables = tables.Select(t => new { t.SchemaName, t.TableName }),
                count = tables.Count
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool, Description("Get detailed information about a table including columns, indexes and constraints")]
    public async Task<string> DescribeTable(
        [Description("Name of the database connection to use")] string database,
        [Description("Name of the table to describe")] string table,
        [Description("Schema name (default: public)")] string schema = "public",
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";

        try
        {
            var tableInfo = await _postgres.GetTableInfoAsync(database, schema, table, ct);
            if (tableInfo is null)
                return $"Error: Table '{schema}.{table}' not found in database '{database}'.";

            return ToolJson.Serialize(new
            {
                database,
                schema = tableInfo.SchemaName,
                table = tableInfo.TableName,
                estimatedRowCount = tableInfo.RowCount,
                columns = tableInfo.Columns.Select(c => new
                {
                    c.Name,
                    c.DataType,
                    c.IsNullable,
                    c.DefaultValue,
                    c.IsPrimaryKey,
                    c.Identity,
                    c.GeneratedExpression,
                    c.Collation
                }),
                indexes = tableInfo.Indexes.Select(i => new
                {
                    i.Name,
                    i.Columns,
                    i.IsUnique,
                    i.IsPrimary,
                    i.Definition
                }),
                // Previously advertised by this tool's description but never populated.
                constraints = tableInfo.Constraints.Select(c => new
                {
                    c.Name,
                    c.Type,
                    c.Definition
                })
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool, Description("Generate a CREATE TABLE statement for an existing table, including constraints and indexes")]
    public async Task<string> GetTableDdl(
        [Description("Name of the database connection to use")] string database,
        [Description("Name of the table")] string table,
        [Description("Schema name (default: public)")] string schema = "public",
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";

        try
        {
            var tableInfo = await _postgres.GetTableInfoAsync(database, schema, table, ct);
            if (tableInfo is null)
                return $"Error: Table '{schema}.{table}' not found.";

            return BuildDdl(schema, table, tableInfo);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Builds DDL from pg_catalog metadata. Types come from format_type so modifiers such as
    /// varchar(255) and numeric(10,2) survive, constraints come from pg_get_constraintdef and
    /// indexes from pg_get_indexdef, so the output round-trips instead of silently dropping
    /// foreign keys, checks, identity columns and expression indexes.
    /// </summary>
    private static string BuildDdl(string schema, string table, TableInfo info)
    {
        var lines = new List<string>();

        foreach (var c in info.Columns)
        {
            var sb = new StringBuilder($"    {SqlText.QuoteIdentifier(c.Name)} {c.DataType}");

            if (!string.IsNullOrEmpty(c.Collation))
                sb.Append($" COLLATE {SqlText.QuoteIdentifier(c.Collation)}");

            if (!string.IsNullOrEmpty(c.GeneratedExpression))
                sb.Append($" GENERATED ALWAYS AS ({c.GeneratedExpression}) STORED");
            else if (c.Identity == "a")
                sb.Append(" GENERATED ALWAYS AS IDENTITY");
            else if (c.Identity == "d")
                sb.Append(" GENERATED BY DEFAULT AS IDENTITY");
            else if (c.DefaultValue is not null)
                sb.Append($" DEFAULT {c.DefaultValue}");

            if (!c.IsNullable) sb.Append(" NOT NULL");

            lines.Add(sb.ToString());
        }

        // pg_get_constraintdef already renders PRIMARY KEY / UNIQUE / CHECK / FOREIGN KEY in full.
        foreach (var constraint in info.Constraints)
            lines.Add($"    CONSTRAINT {SqlText.QuoteIdentifier(constraint.Name)} {constraint.Definition}");

        var ddl = new StringBuilder();
        ddl.AppendLine($"CREATE TABLE {SqlText.QuoteQualified(schema, table)} (");
        ddl.AppendLine(string.Join(",\n", lines));
        ddl.AppendLine(");");

        // Indexes that back a constraint are already emitted above; only standalone ones remain.
        var constraintNames = info.Constraints.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var standalone = info.Indexes
            .Where(i => !i.IsPrimary && !constraintNames.Contains(i.Name))
            .ToList();

        if (standalone.Count > 0)
        {
            ddl.AppendLine();
            foreach (var index in standalone)
                ddl.AppendLine($"{index.Definition};");
        }

        return ddl.ToString().TrimEnd();
    }
}
