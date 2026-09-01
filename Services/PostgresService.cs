using Microsoft.Extensions.Options;
using Npgsql;
using PostgresMcpServer.Models;
using System.Data;
using System.Diagnostics;

namespace PostgresMcpServer.Services;

/// <summary>Metadata recorded alongside a statement in the audit log.</summary>
public record AuditContext(string Operation, string? RiskLevel = null, bool DryRun = false, bool Confirmed = false);

public interface IPostgresService
{
    Task<QueryResult> ExecuteReadOnlyAsync(string database, string query, int maxRows, CancellationToken ct = default);
    Task<QueryResult> ExecuteNonQueryAsync(string database, string query, AuditContext audit, CancellationToken ct = default);
    Task<BatchResult> ExecuteBatchAsync(string database, string[] statements, CancellationToken ct = default);
    Task<List<SchemaInfo>> GetSchemasAsync(string database, CancellationToken ct = default);
    Task<TableInfo?> GetTableInfoAsync(string database, string schema, string table, CancellationToken ct = default);
    Task<List<TableInfo>> GetAllTablesAsync(string database, string? schema = null, CancellationToken ct = default);
    Task<string> GetExplainPlanAsync(string database, string query, bool analyze = false, CancellationToken ct = default);
}

public class PostgresService : IPostgresService
{
    private readonly IConnectionManager _connectionManager;
    private readonly IAuditLogger _auditLogger;
    private readonly LimitSettings _limits;

    public PostgresService(IConnectionManager connectionManager, IAuditLogger auditLogger, IOptions<DatabaseConfig> config)
    {
        _connectionManager = connectionManager;
        _auditLogger = auditLogger;
        _limits = config.Value.Limits;
    }

    private void ApplyTimeout(NpgsqlCommand cmd)
    {
        if (_limits.CommandTimeoutSeconds > 0) cmd.CommandTimeout = _limits.CommandTimeoutSeconds;
    }

    /// <summary>
    /// Audit failures must never discard a database result that already committed, so logging
    /// is isolated here and degrades to a stderr note.
    /// </summary>
    private async Task SafeLogAsync(AuditEntry entry)
    {
        try
        {
            entry.Query = SqlText.RedactSecrets(entry.Query);
            await _auditLogger.LogAsync(entry);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AUDIT-FAILURE] Could not write the audit log: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a statement inside a READ ONLY transaction that is always rolled back. PostgreSQL
    /// itself rejects any write, so read-only-ness does not depend on parsing the SQL correctly.
    /// </summary>
    public async Task<QueryResult> ExecuteReadOnlyAsync(string database, string query, int maxRows, CancellationToken ct = default)
    {
        var result = new QueryResult();
        var sw = Stopwatch.StartNew();
        var entry = new AuditEntry { Database = database, Query = query, Operation = "READ" };

        try
        {
            var dataSource = _connectionManager.GetDataSource(database);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx))
            {
                ApplyTimeout(readOnly);
                await readOnly.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(query, conn, tx))
            {
                ApplyTimeout(cmd);
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                for (var i = 0; i < reader.FieldCount; i++)
                    result.Columns.Add(reader.GetName(i));

                while (await reader.ReadAsync(ct))
                {
                    if (result.Rows.Count >= maxRows)
                    {
                        result.Truncated = true;
                        break;
                    }

                    var row = new Dictionary<string, object?>(reader.FieldCount);
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : ToJsonFriendly(reader.GetValue(i));

                    result.Rows.Add(row);
                }
            }

            await tx.RollbackAsync(ct);

            result.Success = true;
            result.RowsAffected = result.Rows.Count;
            entry.Success = true;
            entry.RowsAffected = result.RowsAffected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            entry.Success = false;
            entry.ErrorMessage = ex.Message;
        }

        sw.Stop();
        result.ExecutionTime = sw.Elapsed;
        await SafeLogAsync(entry);
        return result;
    }

    public async Task<QueryResult> ExecuteNonQueryAsync(string database, string query, AuditContext audit, CancellationToken ct = default)
    {
        var result = new QueryResult();
        var sw = Stopwatch.StartNew();
        var entry = new AuditEntry
        {
            Database = database,
            Query = query,
            Operation = audit.Operation,
            RiskLevel = audit.RiskLevel,
            DryRun = audit.DryRun,
            Confirmed = audit.Confirmed
        };

        try
        {
            var dataSource = _connectionManager.GetDataSource(database);
            await using var cmd = dataSource.CreateCommand(query);
            ApplyTimeout(cmd);
            result.RowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            result.Success = true;
            entry.Success = true;
            entry.RowsAffected = result.RowsAffected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            entry.Success = false;
            entry.ErrorMessage = ex.Message;
        }

        sw.Stop();
        result.ExecutionTime = sw.Elapsed;
        await SafeLogAsync(entry);
        return result;
    }

    /// <summary>
    /// Runs each statement as its own command inside one real transaction, so a failure rolls
    /// back everything and no caller-supplied COMMIT can escape the transaction boundary.
    /// </summary>
    public async Task<BatchResult> ExecuteBatchAsync(string database, string[] statements, CancellationToken ct = default)
    {
        var result = new BatchResult();
        var sw = Stopwatch.StartNew();
        var dataSource = _connectionManager.GetDataSource(database);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var index = 0;
        try
        {
            foreach (var statement in statements)
            {
                await using var cmd = new NpgsqlCommand(statement, conn, tx);
                ApplyTimeout(cmd);
                var affected = await cmd.ExecuteNonQueryAsync(ct);

                result.Statements.Add(new BatchStatementResult
                {
                    Index = index,
                    Sql = SqlText.RedactSecrets(statement),
                    RowsAffected = affected
                });

                await SafeLogAsync(new AuditEntry
                {
                    Database = database,
                    Query = statement,
                    Operation = "BATCH",
                    Success = true,
                    RowsAffected = affected
                });

                index++;
            }

            await tx.CommitAsync(ct);
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            await SafeRollbackAsync(tx);
            throw;
        }
        catch (Exception ex)
        {
            await SafeRollbackAsync(tx);
            result.Success = false;
            result.RolledBack = true;
            result.ErrorMessage = ex.Message;
            result.FailedStatementIndex = index;

            await SafeLogAsync(new AuditEntry
            {
                Database = database,
                Query = index < statements.Length ? statements[index] : string.Empty,
                Operation = "BATCH",
                Success = false,
                ErrorMessage = ex.Message
            });
        }

        sw.Stop();
        result.ExecutionTime = sw.Elapsed;
        return result;
    }

    private static async Task SafeRollbackAsync(NpgsqlTransaction tx)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ROLLBACK-FAILURE] {ex.Message}");
        }
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
        ApplyTimeout(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            schemas.Add(new SchemaInfo { SchemaName = reader.GetString(0) });

        return schemas;
    }

    public async Task<TableInfo?> GetTableInfoAsync(string database, string schema, string table, CancellationToken ct = default)
    {
        var dataSource = _connectionManager.GetDataSource(database);
        var tableInfo = new TableInfo { TableName = table, SchemaName = schema };

        // Everything below is scoped by the relation's own OID, which removes the cross-schema
        // constraint-name collision that the previous information_schema joins were prone to.
        const string columnsQuery = """
            SELECT
                a.attname,
                format_type(a.atttypid, a.atttypmod) AS data_type,
                NOT a.attnotnull                     AS is_nullable,
                pg_get_expr(d.adbin, d.adrelid)      AS column_default,
                a.attnum                             AS ordinal_position,
                EXISTS (
                    SELECT 1 FROM pg_index i
                    WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY (i.indkey)
                )                                    AS is_pk,
                NULLIF(a.attidentity, '')::text      AS identity,
                CASE WHEN a.attgenerated <> '' THEN pg_get_expr(d.adbin, d.adrelid) END AS generated_expr,
                (SELECT collname FROM pg_collation cl
                  WHERE cl.oid = a.attcollation AND cl.collname <> 'default') AS collation
            FROM pg_attribute a
            JOIN pg_class c      ON c.oid = a.attrelid
            JOIN pg_namespace n  ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE n.nspname = $1 AND c.relname = $2
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """;

        await using (var colCmd = dataSource.CreateCommand(columnsQuery))
        {
            ApplyTimeout(colCmd);
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
                    OrdinalPosition = colReader.GetInt16(4),
                    IsPrimaryKey = colReader.GetBoolean(5),
                    Identity = colReader.IsDBNull(6) ? null : colReader.GetString(6),
                    GeneratedExpression = colReader.IsDBNull(7) ? null : colReader.GetString(7),
                    Collation = colReader.IsDBNull(8) ? null : colReader.GetString(8)
                });
            }
        }

        if (tableInfo.Columns.Count == 0)
            return null;

        // pg_get_indexdef round-trips expression, partial, INCLUDE and operator-class indexes.
        const string indexQuery = """
            SELECT
                i.relname,
                pg_get_indexdef(ix.indexrelid) AS definition,
                ix.indisunique,
                ix.indisprimary,
                ARRAY(
                    SELECT pg_get_indexdef(ix.indexrelid, k, true)
                    FROM generate_series(1, ix.indnatts) AS k
                ) AS columns
            FROM pg_index ix
            JOIN pg_class i     ON i.oid = ix.indexrelid
            JOIN pg_class t     ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = $1 AND t.relname = $2
            ORDER BY i.relname
            """;

        await using (var idxCmd = dataSource.CreateCommand(indexQuery))
        {
            ApplyTimeout(idxCmd);
            idxCmd.Parameters.AddWithValue(schema);
            idxCmd.Parameters.AddWithValue(table);
            await using var idxReader = await idxCmd.ExecuteReaderAsync(ct);

            while (await idxReader.ReadAsync(ct))
            {
                tableInfo.Indexes.Add(new IndexInfo
                {
                    Name = idxReader.GetString(0),
                    Definition = idxReader.GetString(1),
                    IsUnique = idxReader.GetBoolean(2),
                    IsPrimary = idxReader.GetBoolean(3),
                    Columns = [.. idxReader.GetFieldValue<string[]>(4)]
                });
            }
        }

        const string constraintQuery = """
            SELECT
                con.conname,
                CASE con.contype
                    WHEN 'p' THEN 'PRIMARY KEY'
                    WHEN 'f' THEN 'FOREIGN KEY'
                    WHEN 'u' THEN 'UNIQUE'
                    WHEN 'c' THEN 'CHECK'
                    WHEN 'x' THEN 'EXCLUSION'
                    ELSE con.contype::text
                END AS constraint_type,
                pg_get_constraintdef(con.oid) AS definition
            FROM pg_constraint con
            JOIN pg_class c     ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = $1 AND c.relname = $2
            ORDER BY con.contype, con.conname
            """;

        await using (var conCmd = dataSource.CreateCommand(constraintQuery))
        {
            ApplyTimeout(conCmd);
            conCmd.Parameters.AddWithValue(schema);
            conCmd.Parameters.AddWithValue(table);
            await using var conReader = await conCmd.ExecuteReaderAsync(ct);

            while (await conReader.ReadAsync(ct))
            {
                tableInfo.Constraints.Add(new ConstraintInfo
                {
                    Name = conReader.GetString(0),
                    Type = conReader.GetString(1),
                    Definition = conReader.GetString(2)
                });
            }
        }

        const string countQuery = """
            SELECT c.reltuples::bigint
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = $1 AND c.relname = $2
            """;

        await using (var countCmd = dataSource.CreateCommand(countQuery))
        {
            ApplyTimeout(countCmd);
            countCmd.Parameters.AddWithValue(schema);
            countCmd.Parameters.AddWithValue(table);
            tableInfo.RowCount = await countCmd.ExecuteScalarAsync(ct) as long?;
        }

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

        if (schema != null) query += " AND table_schema = $1";
        query += " ORDER BY table_schema, table_name";

        await using var cmd = dataSource.CreateCommand(query);
        ApplyTimeout(cmd);
        if (schema != null) cmd.Parameters.AddWithValue(schema);

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

    /// <summary>
    /// EXPLAIN ANALYZE executes the statement it is given, so the plan is always produced inside
    /// a transaction that is rolled back. A DML statement can be analysed without persisting it.
    /// </summary>
    public async Task<string> GetExplainPlanAsync(string database, string query, bool analyze = false, CancellationToken ct = default)
    {
        var dataSource = _connectionManager.GetDataSource(database);
        var explainQuery = analyze
            ? $"EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) {query}"
            : $"EXPLAIN (FORMAT TEXT) {query}";

        var lines = new List<string>();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (!analyze)
            {
                await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx);
                ApplyTimeout(readOnly);
                await readOnly.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = new NpgsqlCommand(explainQuery, conn, tx);
            ApplyTimeout(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                lines.Add(reader.GetString(0));
        }
        finally
        {
            await SafeRollbackAsync(tx);
        }

        await SafeLogAsync(new AuditEntry
        {
            Database = database,
            Query = query,
            Operation = analyze ? "EXPLAIN ANALYZE (rolled back)" : "EXPLAIN",
            Success = true
        });

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Npgsql surfaces provider-specific CLR types that System.Text.Json cannot always serialize.
    /// Anything outside the JSON-native set is rendered as its string form instead of throwing.
    /// </summary>
    private static object? ToJsonFriendly(object value) => value switch
    {
        null => null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or string or DateTime or DateTimeOffset
            or Guid or char => value,
        byte[] bytes => Convert.ToBase64String(bytes),
        System.Text.Json.JsonDocument doc => doc.RootElement.Clone(),
        Array array => array.Cast<object?>().Select(v => v is null ? null : ToJsonFriendly(v)).ToList(),
        _ => value.ToString()
    };
}
