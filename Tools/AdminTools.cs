using ModelContextProtocol.Server;
using PostgresMcpServer.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public class AdminTools
{
    private readonly IPostgresService _postgres;
    private readonly IConnectionManager _connectionManager;
    private readonly ISafetyGuard _safetyGuard;

    public AdminTools(IPostgresService postgres, IConnectionManager connectionManager, ISafetyGuard safetyGuard)
    {
        _postgres = postgres;
        _connectionManager = connectionManager;
        _safetyGuard = safetyGuard;
    }

    [McpServerTool, Description("Create a new table in the database")]
    public async Task<string> CreateTable(
        [Description("Name of the database connection to use")] string database,
        [Description("Name of the new table")] string tableName,
        [Description("Column definitions as JSON array, e.g. [{\"name\":\"id\",\"type\":\"SERIAL\",\"primaryKey\":true},{\"name\":\"email\",\"type\":\"VARCHAR(255)\",\"nullable\":false}]")] string columns,
        [Description("Schema name (default: public)")] string schema = "public",
        [Description("Set to true to preview the DDL without executing")] bool dryRun = false,
        [Description("Confirm execution")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found.";
        }

        List<ColumnDef>? columnDefs;
        try
        {
            columnDefs = JsonSerializer.Deserialize<List<ColumnDef>>(columns, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            return $"Error parsing columns JSON: {ex.Message}";
        }

        if (columnDefs == null || columnDefs.Count == 0)
        {
            return "Error: At least one column definition is required.";
        }

        var colSql = columnDefs.Select(c =>
        {
            var def = $"{c.Name} {c.Type}";
            if (c.PrimaryKey) def += " PRIMARY KEY";
            else if (!c.Nullable) def += " NOT NULL";
            if (!string.IsNullOrEmpty(c.Default)) def += $" DEFAULT {c.Default}";
            return def;
        });

        var sql = $"CREATE TABLE {schema}.{tableName} (\n    {string.Join(",\n    ", colSql)}\n)";

        if (dryRun)
        {
            return $"[DRY RUN] Would execute:\n\n{sql}";
        }

        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                requiresConfirmation = true,
                operation = "CREATE TABLE",
                ddl = sql,
                message = "Set confirm=true to create the table."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _postgres.ExecuteNonQueryAsync(database, sql, ct);

        return result.Success
            ? $"Table '{schema}.{tableName}' created successfully."
            : $"Error: {result.ErrorMessage}";
    }

    [McpServerTool, Description("Drop a table from the database")]
    public async Task<string> DropTable(
        [Description("Name of the database connection to use")] string database,
        [Description("Name of the table to drop")] string tableName,
        [Description("Schema name (default: public)")] string schema = "public",
        [Description("Add CASCADE to drop dependent objects")] bool cascade = false,
        [Description("Set to true to preview without executing")] bool dryRun = false,
        [Description("Confirm this destructive operation")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found.";
        }

        var sql = $"DROP TABLE {schema}.{tableName}{(cascade ? " CASCADE" : "")}";

        if (dryRun)
        {
            return _safetyGuard.GetDryRunMessage(sql, database);
        }

        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                requiresConfirmation = true,
                riskLevel = "CRITICAL",
                operation = "DROP TABLE",
                warning = $"This will permanently delete table '{schema}.{tableName}' and all its data!",
                ddl = sql,
                message = "Set confirm=true to proceed with this destructive operation."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _postgres.ExecuteNonQueryAsync(database, sql, ct);

        return result.Success
            ? $"Table '{schema}.{tableName}' dropped successfully."
            : $"Error: {result.ErrorMessage}";
    }

    [McpServerTool, Description("List all database roles/users")]
    public async Task<string> ListRoles(
        [Description("Name of the database connection to use")] string database,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found.";
        }

        var sql = """
            SELECT
                rolname as role_name,
                rolsuper as is_superuser,
                rolcreaterole as can_create_roles,
                rolcreatedb as can_create_db,
                rolcanlogin as can_login,
                rolconnlimit as connection_limit
            FROM pg_roles
            WHERE rolname NOT LIKE 'pg_%'
            ORDER BY rolname
            """;

        var result = await _postgres.ExecuteQueryAsync(database, sql, ct);

        return result.Success
            ? JsonSerializer.Serialize(result.Rows, new JsonSerializerOptions { WriteIndented = true })
            : $"Error: {result.ErrorMessage}";
    }

    [McpServerTool, Description("Create a new database role/user")]
    public async Task<string> CreateRole(
        [Description("Name of the database connection to use")] string database,
        [Description("Name of the new role")] string roleName,
        [Description("Password for the role (required if canLogin is true)")] string? password = null,
        [Description("Allow role to log in")] bool canLogin = true,
        [Description("Allow role to create databases")] bool canCreateDb = false,
        [Description("Allow role to create other roles")] bool canCreateRoles = false,
        [Description("Set to true to preview without executing")] bool dryRun = false,
        [Description("Confirm execution")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found.";
        }

        var options = new List<string>();
        if (canLogin)
        {
            options.Add("LOGIN");
            if (!string.IsNullOrEmpty(password))
            {
                options.Add($"PASSWORD '{password}'");
            }
        }
        if (canCreateDb) options.Add("CREATEDB");
        if (canCreateRoles) options.Add("CREATEROLE");

        var sql = $"CREATE ROLE {roleName} {string.Join(" ", options)}";

        if (dryRun)
        {
            // Mask password in dry run output
            var displaySql = password != null ? sql.Replace(password, "********") : sql;
            return $"[DRY RUN] Would execute:\n\n{displaySql}";
        }

        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                requiresConfirmation = true,
                operation = "CREATE ROLE",
                roleName,
                canLogin,
                canCreateDb,
                canCreateRoles,
                message = "Set confirm=true to create the role."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _postgres.ExecuteNonQueryAsync(database, sql, ct);

        return result.Success
            ? $"Role '{roleName}' created successfully."
            : $"Error: {result.ErrorMessage}";
    }

    [McpServerTool, Description("Grant privileges on a table to a role")]
    public async Task<string> GrantPrivileges(
        [Description("Name of the database connection to use")] string database,
        [Description("Table name")] string tableName,
        [Description("Role to grant privileges to")] string roleName,
        [Description("Privileges to grant (e.g., SELECT, INSERT, UPDATE, DELETE, ALL)")] string privileges = "SELECT",
        [Description("Schema name (default: public)")] string schema = "public",
        [Description("Set to true to preview without executing")] bool dryRun = false,
        [Description("Confirm execution")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found.";
        }

        var sql = $"GRANT {privileges} ON {schema}.{tableName} TO {roleName}";

        if (dryRun)
        {
            return $"[DRY RUN] Would execute:\n\n{sql}";
        }

        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                requiresConfirmation = true,
                operation = "GRANT",
                privileges,
                table = $"{schema}.{tableName}",
                role = roleName,
                message = "Set confirm=true to grant privileges."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var result = await _postgres.ExecuteNonQueryAsync(database, sql, ct);

        return result.Success
            ? $"Granted {privileges} on {schema}.{tableName} to {roleName}."
            : $"Error: {result.ErrorMessage}";
    }

    [McpServerTool, Description("Get database statistics and health information")]
    public async Task<string> GetDatabaseStats(
        [Description("Name of the database connection to use")] string database,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
        {
            return $"Error: Database '{database}' not found.";
        }

        var sql = """
            SELECT
                pg_database.datname as database_name,
                pg_size_pretty(pg_database_size(pg_database.datname)) as size,
                numbackends as active_connections,
                xact_commit as transactions_committed,
                xact_rollback as transactions_rolled_back,
                blks_read as disk_blocks_read,
                blks_hit as buffer_cache_hits,
                ROUND(100.0 * blks_hit / NULLIF(blks_hit + blks_read, 0), 2) as cache_hit_ratio
            FROM pg_stat_database
            JOIN pg_database ON pg_database.oid = pg_stat_database.datid
            WHERE pg_database.datname = current_database()
            """;

        var result = await _postgres.ExecuteQueryAsync(database, sql, ct);

        return result.Success
            ? JsonSerializer.Serialize(result.Rows.FirstOrDefault(), new JsonSerializerOptions { WriteIndented = true })
            : $"Error: {result.ErrorMessage}";
    }

    private class ColumnDef
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool PrimaryKey { get; set; }
        public bool Nullable { get; set; } = true;
        public string? Default { get; set; }
    }
}
