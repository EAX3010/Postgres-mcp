using ModelContextProtocol.Server;
using PostgresMcpServer.Services;
using System.ComponentModel;
using System.Text.Json;

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
        {
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";
        }

        var schemas = await _postgres.GetSchemasAsync(database, ct);
        return JsonSerializer.Serialize(new
        {
            database,
            schemas = schemas.Select(s => s.SchemaName),
            count = schemas.Count
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("List all tables in the database or a specific schema")]
    public async Task<string> ListTables(
        [Description("Name of the database connection to use")] string database,
        [Description("Optional: filter by schema name")] string? schema = null,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";
        }

        var tables = await _postgres.GetAllTablesAsync(database, schema, ct);
        return JsonSerializer.Serialize(new
        {
            database,
            schema = schema ?? "all",
            tables = tables.Select(t => new { t.SchemaName, t.TableName }),
            count = tables.Count
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get detailed information about a table including columns, indexes, and constraints")]
    public async Task<string> DescribeTable(
        [Description("Name of the database connection to use")] string database,
        [Description("Name of the table to describe")] string table,
        [Description("Schema name (default: public)")] string schema = "public",
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";
        }

        var tableInfo = await _postgres.GetTableInfoAsync(database, schema, table, ct);

        if (tableInfo == null)
        {
            return $"Error: Table '{schema}.{table}' not found in database '{database}'.";
        }

        return JsonSerializer.Serialize(new
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
                c.IsPrimaryKey
            }),
            indexes = tableInfo.Indexes.Select(i => new
            {
                i.Name,
                i.Columns,
                i.IsUnique,
                i.IsPrimary
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get a CREATE TABLE statement for an existing table (reverse engineering)")]
    public async Task<string> GetTableDdl(
        [Description("Name of the database connection to use")] string database,
        [Description("Name of the table")] string table,
        [Description("Schema name (default: public)")] string schema = "public",
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found. Available: {string.Join(", ", _connectionManager.GetDatabaseNames())}";
        }

        var tableInfo = await _postgres.GetTableInfoAsync(database, schema, table, ct);

        if (tableInfo == null)
        {
            return $"Error: Table '{schema}.{table}' not found.";
        }

        // Generate CREATE TABLE DDL
        var columnDefs = tableInfo.Columns.Select(c =>
        {
            var def = $"    {c.Name} {c.DataType}";
            if (!c.IsNullable) def += " NOT NULL";
            if (c.DefaultValue != null) def += $" DEFAULT {c.DefaultValue}";
            return def;
        });

        var pkColumns = tableInfo.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        var constraints = new List<string>();
        if (pkColumns.Count > 0)
        {
            constraints.Add($"    PRIMARY KEY ({string.Join(", ", pkColumns)})");
        }

        var allDefs = columnDefs.Concat(constraints);

        var ddl = $"""
            CREATE TABLE {schema}.{table} (
            {string.Join(",\n", allDefs)}
            );
            """;

        // Add index statements
        var indexDdl = tableInfo.Indexes
            .Where(i => !i.IsPrimary)
            .Select(i => $"CREATE {(i.IsUnique ? "UNIQUE " : "")}INDEX {i.Name} ON {schema}.{table} ({string.Join(", ", i.Columns)});");

        if (indexDdl.Any())
        {
            ddl += "\n\n" + string.Join("\n", indexDdl);
        }

        return ddl;
    }
}
