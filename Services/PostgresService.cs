using Npgsql;
using PostgresMcpServer.Models;
using System.Diagnostics;

namespace PostgresMcpServer.Services;

public interface IPostgresService
{
    Task<QueryResult> ExecuteQueryAsync(string database, string query, CancellationToken ct = default);
    Task<QueryResult> ExecuteNonQueryAsync(string database, string query, CancellationToken ct = default);
    Task<List<SchemaInfo>> GetSchemasAsync(string database, CancellationToken ct = default);
    Task<TableInfo?> GetTableInfoAsync(string database, string schema, string table, CancellationToken ct = default);
    Task<List<TableInfo>> GetAllTablesAsync(string database, string? schema = null, CancellationToken ct = default);
    Task<string> GetExplainPlanAsync(string database, string query, bool analyze = false, CancellationToken ct = default);
}

public class PostgresService : IPostgresService
{
    private readonly IConnectionManager _connectionManager;
    private readonly IAuditLogger _auditLogger;

    public PostgresService(IConnectionManager connectionManager, IAuditLogger auditLogger)
    {
        _connectionManager = connectionManager;
        _auditLogger = auditLogger;
    }

    public async Task<QueryResult> ExecuteQueryAsync(string database, string query, CancellationToken ct = default)
    {
        var result = new QueryResult();
        var sw = Stopwatch.StartNew();
        var entry = new AuditEntry { Database = database, Query = query, Operation = "SELECT" };

        try
        {
            var dataSource = _connectionManager.GetDataSource(database);
            await using var cmd = dataSource.CreateCommand(query);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // Get column names
            for (int i = 0; i < reader.FieldCount; i++)
            {
                result.Columns.Add(reader.GetName(i));
            }

            // Read rows
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                result.Rows.Add(row);
            }

            result.Success = true;
            result.RowsAffected = result.Rows.Count;
            entry.Success = true;
            entry.RowsAffected = result.RowsAffected;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            entry.Success = false;
            entry.ErrorMessage = ex.Message;
        }

        sw.Stop();
        result.ExecutionTime = sw.Elapsed;
        await _auditLogger.LogAsync(entry);
        return result;
    }

    public async Task<QueryResult> ExecuteNonQueryAsync(string database, string query, CancellationToken ct = default)
    {
        var result = new QueryResult();
        var sw = Stopwatch.StartNew();
        var entry = new AuditEntry { Database = database, Query = query, Operation = "EXECUTE" };

        try
        {
            var dataSource = _connectionManager.GetDataSource(database);
            await using var cmd = dataSource.CreateCommand(query);
            result.RowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            result.Success = true;
            entry.Success = true;
            entry.RowsAffected = result.RowsAffected;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            entry.Success = false;
            entry.ErrorMessage = ex.Message;
        }

        sw.Stop();
        result.ExecutionTime = sw.Elapsed;
        await _auditLogger.LogAsync(entry);
        return result;
    }

    public async Task<List<SchemaInfo>> GetSchemasAsync(string database, CancellationToken ct = default)
    {
        var schemas = new List<SchemaInfo>();
        var dataSource = _connectionManager.GetDataSource(database);

        const string query = """
            SELECT schema_name
            FROM information_schema.schemata
            WHERE schema_name NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY schema_name
            """;

        await using var cmd = dataSource.CreateCommand(query);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            schemas.Add(new SchemaInfo { SchemaName = reader.GetString(0) });
        }

        return schemas;
    }

    public async Task<TableInfo?> GetTableInfoAsync(string database, string schema, string table, CancellationToken ct = default)
    {
        var dataSource = _connectionManager.GetDataSource(database);
        var tableInfo = new TableInfo { TableName = table, SchemaName = schema };

        // Get columns
        const string columnsQuery = """
            SELECT
                c.column_name,
                c.data_type,
                c.is_nullable = 'YES' as is_nullable,
                c.column_default,
                c.ordinal_position,
                CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END as is_pk
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
                WHERE tc.table_schema = $1 AND tc.table_name = $2 AND tc.constraint_type = 'PRIMARY KEY'
            ) pk ON c.column_name = pk.column_name
            WHERE c.table_schema = $1 AND c.table_name = $2
            ORDER BY c.ordinal_position
            """;

        await using var colCmd = dataSource.CreateCommand(columnsQuery);
        colCmd.Parameters.AddWithValue(schema);
        colCmd.Parameters.AddWithValue(table);
        await using var colReader = await colCmd.ExecuteReaderAsync(ct);

        while (await colReader.ReadAsync(ct))
        {
            tableInfo.Columns.Add(new ColumnInfo
            {
                Name = colReader.GetString(0),
                DataType = colReader.GetString(1),
                IsNullable = colReader.GetBoolean(2),
                DefaultValue = colReader.IsDBNull(3) ? null : colReader.GetString(3),
                OrdinalPosition = colReader.GetInt32(4),
                IsPrimaryKey = colReader.GetBoolean(5)
            });
        }

        if (tableInfo.Columns.Count == 0)
            return null;

        // Get indexes
        const string indexQuery = """
            SELECT
                i.relname as index_name,
                array_agg(a.attname ORDER BY array_position(ix.indkey, a.attnum)) as columns,
                ix.indisunique,
                ix.indisprimary
            FROM pg_class t
            JOIN pg_index ix ON t.oid = ix.indrelid
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = $1 AND t.relname = $2
            GROUP BY i.relname, ix.indisunique, ix.indisprimary
            """;

        await using var idxCmd = dataSource.CreateCommand(indexQuery);
        idxCmd.Parameters.AddWithValue(schema);
        idxCmd.Parameters.AddWithValue(table);
        await using var idxReader = await idxCmd.ExecuteReaderAsync(ct);

        while (await idxReader.ReadAsync(ct))
        {
            var columns = idxReader.GetFieldValue<string[]>(1);
            tableInfo.Indexes.Add(new IndexInfo
            {
                Name = idxReader.GetString(0),
                Columns = columns.ToList(),
                IsUnique = idxReader.GetBoolean(2),
                IsPrimary = idxReader.GetBoolean(3)
            });
        }

        // Get row count estimate
        const string countQuery = """
            SELECT reltuples::bigint
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = $1 AND c.relname = $2
            """;

        await using var countCmd = dataSource.CreateCommand(countQuery);
        countCmd.Parameters.AddWithValue(schema);
        countCmd.Parameters.AddWithValue(table);
        var count = await countCmd.ExecuteScalarAsync(ct);
        tableInfo.RowCount = count as long?;

        return tableInfo;
    }

    public async Task<List<TableInfo>> GetAllTablesAsync(string database, string? schema = null, CancellationToken ct = default)
    {
        var tables = new List<TableInfo>();
        var dataSource = _connectionManager.GetDataSource(database);

        var query = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
            AND table_schema NOT IN ('pg_catalog', 'information_schema')
            """;

        if (schema != null)
        {
            query += " AND table_schema = $1";
        }
        query += " ORDER BY table_schema, table_name";

        await using var cmd = dataSource.CreateCommand(query);
        if (schema != null)
        {
            cmd.Parameters.AddWithValue(schema);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            tables.Add(new TableInfo
            {
                SchemaName = reader.GetString(0),
                TableName = reader.GetString(1)
            });
        }

        return tables;
    }

    public async Task<string> GetExplainPlanAsync(string database, string query, bool analyze = false, CancellationToken ct = default)
    {
        var dataSource = _connectionManager.GetDataSource(database);
        var explainQuery = analyze
            ? $"EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) {query}"
            : $"EXPLAIN (FORMAT TEXT) {query}";

        await using var cmd = dataSource.CreateCommand(explainQuery);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var lines = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join("\n", lines);
    }
}
