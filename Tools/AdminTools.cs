using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public partial class AdminTools
{
    private readonly IPostgresService _postgres;
    private readonly IConnectionManager _connectionManager;
    private readonly ISafetyGuard _safetyGuard;
    private readonly LimitSettings _limits;

    public AdminTools(
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

    /// <summary>Privileges PostgreSQL accepts for a table GRANT.</summary>
    private static readonly string[] AllowedPrivileges =
    [
        "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE",
        "REFERENCES", "TRIGGER", "MAINTAIN", "ALL", "ALL PRIVILEGES"
    ];

    // A type name may contain letters, digits, spaces, parentheses, commas, brackets and dots
    // (e.g. "numeric(10,2)", "character varying(255)", "text[]", "public.my_enum") - nothing else.
    [GeneratedRegex(@"^[A-Za-z0-9_ ,\(\)\[\]\.]+$")]
    private static partial Regex SafeTypeRegex();

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
            return $"Error: Database '{database}' not found.";

        List<ColumnDef>? columnDefs;
        try
        {
            columnDefs = JsonSerializer.Deserialize<List<ColumnDef>>(
                columns, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            return $"Error parsing columns JSON: {ex.Message}";
        }

        if (columnDefs is null || columnDefs.Count == 0)
            return "Error: At least one column definition is required.";

        string sql;
        try
        {
            var colSql = columnDefs.Select(c =>
            {
                if (string.IsNullOrWhiteSpace(c.Type) || !SafeTypeRegex().IsMatch(c.Type))
                    throw new ArgumentException($"Column '{c.Name}' has an invalid type: '{c.Type}'.");

                var def = $"{SqlText.QuoteIdentifier(c.Name)} {c.Type.Trim()}";
                if (c.PrimaryKey) def += " PRIMARY KEY";
                else if (!c.Nullable) def += " NOT NULL";

                if (!string.IsNullOrWhiteSpace(c.Default))
                {
                    // A DEFAULT is an arbitrary expression, so it cannot be quoted as an
                    // identifier. It must at least not smuggle in extra statements.
                    if (SqlText.IsMultiStatement(c.Default))
                        throw new ArgumentException($"Column '{c.Name}' has a DEFAULT containing multiple statements.");
                    def += $" DEFAULT {c.Default.Trim()}";
                }

                return def;
            }).ToList();

            sql = $"CREATE TABLE {SqlText.QuoteQualified(schema, tableName)} (\n    {string.Join(",\n    ", colSql)}\n)";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }

        if (dryRun)
        {
            if (!_safetyGuard.Settings.EnableDryRun)
                return "Error: Dry-run previews are disabled by configuration (Safety.EnableDryRun=false).";
            return $"[DRY RUN] Would execute:\n\n{sql}";
        }

        if (!confirm)
        {
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                operation = "CREATE TABLE",
                ddl = sql,
                message = "Set confirm=true to create the table."
            });
        }

        var result = await _postgres.ExecuteNonQueryAsync(
            database, sql, new AuditContext("CREATE TABLE", RiskLevel.Medium.ToWireString(), false, true), ct);

        return result.Success
            ? $"Table {SqlText.QuoteQualified(schema, tableName)} created successfully."
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
            return $"Error: Database '{database}' not found.";

        string sql;
        try
        {
            sql = $"DROP TABLE {SqlText.QuoteQualified(schema, tableName)}{(cascade ? " CASCADE" : "")}";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }

        if (dryRun)
        {
            if (!_safetyGuard.Settings.EnableDryRun)
                return "Error: Dry-run previews are disabled by configuration (Safety.EnableDryRun=false).";
            return _safetyGuard.GetDryRunMessage(sql, database);
        }

        if (!confirm)
        {
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                riskLevel = RiskLevel.Critical.ToWireString(),
                operation = "DROP TABLE",
                warning = $"This permanently deletes table {SqlText.QuoteQualified(schema, tableName)} and all its data." +
                          (cascade ? " CASCADE will also drop every dependent object." : ""),
                ddl = sql,
                message = "Set confirm=true to proceed with this destructive operation."
            });
        }

        var result = await _postgres.ExecuteNonQueryAsync(
            database, sql, new AuditContext("DROP TABLE", RiskLevel.Critical.ToWireString(), false, true), ct);

        return result.Success
            ? $"Table {SqlText.QuoteQualified(schema, tableName)} dropped successfully."
            : $"Error: {result.ErrorMessage}";
    }

    [McpServerTool, Description("List all database roles/users")]
    public async Task<string> ListRoles(
        [Description("Name of the database connection to use")] string database,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found.";

        const string sql = """
            SELECT
                rolname          AS role_name,
                rolsuper         AS is_superuser,
                rolcreaterole    AS can_create_roles,
                rolcreatedb      AS can_create_db,
                rolcanlogin      AS can_login,
                rolconnlimit     AS connection_limit
            FROM pg_roles
            WHERE rolname NOT LIKE 'pg\_%'
            ORDER BY rolname
            """;

        try
        {
            var result = await _postgres.ExecuteReadOnlyAsync(database, sql, _limits.MaxRows, ct);
            return result.Success ? ToolJson.Serialize(result.Rows) : $"Error: {result.ErrorMessage}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
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
            return $"Error: Database '{database}' not found.";

        string sql;
        try
        {
            var options = new List<string>();
            if (canLogin)
            {
                options.Add("LOGIN");
                if (!string.IsNullOrEmpty(password))
                    options.Add($"PASSWORD {SqlText.QuoteLiteral(password)}");
            }
            if (canCreateDb) options.Add("CREATEDB");
            if (canCreateRoles) options.Add("CREATEROLE");

            sql = $"CREATE ROLE {SqlText.QuoteIdentifier(roleName)} {string.Join(" ", options)}".TrimEnd();
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }

        if (dryRun)
        {
            if (!_safetyGuard.Settings.EnableDryRun)
                return "Error: Dry-run previews are disabled by configuration (Safety.EnableDryRun=false).";
            return $"[DRY RUN] Would execute:\n\n{SqlText.RedactSecrets(sql)}";
        }

        if (!confirm)
        {
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                operation = "CREATE ROLE",
                roleName,
                canLogin,
                canCreateDb,
                canCreateRoles,
                message = "Set confirm=true to create the role."
            });
        }

        // The audit logger redacts PASSWORD literals, so the credential never reaches the log.
        var result = await _postgres.ExecuteNonQueryAsync(
            database, sql, new AuditContext("CREATE ROLE", RiskLevel.High.ToWireString(), false, true), ct);

        return result.Success
            ? $"Role '{roleName}' created successfully."
            : $"Error: {result.ErrorMessage}";
    }

    [McpServerTool, Description("Grant privileges on a table to a role")]
    public async Task<string> GrantPrivileges(
        [Description("Name of the database connection to use")] string database,
        [Description("Table name")] string tableName,
        [Description("Role to grant privileges to")] string roleName,
        [Description("Privileges to grant, comma separated (SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, ALL)")] string privileges = "SELECT",
        [Description("Schema name (default: public)")] string schema = "public",
        [Description("Set to true to preview without executing")] bool dryRun = false,
        [Description("Confirm execution")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found.";

        string sql;
        string normalizedPrivileges;
        try
        {
            normalizedPrivileges = NormalizePrivileges(privileges);
            sql = $"GRANT {normalizedPrivileges} ON TABLE {SqlText.QuoteQualified(schema, tableName)} TO {SqlText.QuoteIdentifier(roleName)}";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }

        if (dryRun)
        {
            if (!_safetyGuard.Settings.EnableDryRun)
                return "Error: Dry-run previews are disabled by configuration (Safety.EnableDryRun=false).";
            return $"[DRY RUN] Would execute:\n\n{sql}";
        }

        if (!confirm)
        {
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                operation = "GRANT",
                privileges = normalizedPrivileges,
                table = $"{schema}.{tableName}",
                role = roleName,
                message = "Set confirm=true to grant privileges."
            });
        }

        var result = await _postgres.ExecuteNonQueryAsync(
            database, sql, new AuditContext("GRANT", RiskLevel.High.ToWireString(), false, true), ct);

        return result.Success
            ? $"Granted {normalizedPrivileges} on {schema}.{tableName} to {roleName}."
            : $"Error: {result.ErrorMessage}";
    }

    /// <summary>Validates privileges against an allowlist; they cannot be quoted as identifiers.</summary>
    internal static string NormalizePrivileges(string privileges)
    {
        if (string.IsNullOrWhiteSpace(privileges))
            throw new ArgumentException("At least one privilege is required.");

        var parts = privileges
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Regex.Replace(p.ToUpperInvariant(), @"\s+", " "))
            .ToList();

        var invalid = parts.Where(p => !AllowedPrivileges.Contains(p)).ToList();
        if (invalid.Count > 0)
            throw new ArgumentException(
                $"Unsupported privilege(s): {string.Join(", ", invalid)}. Allowed: {string.Join(", ", AllowedPrivileges)}.");

        return string.Join(", ", parts.Distinct());
    }

    [McpServerTool, Description("Get database statistics and health information")]
    public async Task<string> GetDatabaseStats(
        [Description("Name of the database connection to use")] string database,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found.";

        const string sql = """
            SELECT
                pg_database.datname AS database_name,
                pg_size_pretty(pg_database_size(pg_database.datname)) AS size,
                numbackends   AS active_connections,
                xact_commit   AS transactions_committed,
                xact_rollback AS transactions_rolled_back,
                blks_read     AS disk_blocks_read,
                blks_hit      AS buffer_cache_hits,
                ROUND(100.0 * blks_hit / NULLIF(blks_hit + blks_read, 0), 2) AS cache_hit_ratio
            FROM pg_stat_database
            JOIN pg_database ON pg_database.oid = pg_stat_database.datid
            WHERE pg_database.datname = current_database()
            """;

        try
        {
            var result = await _postgres.ExecuteReadOnlyAsync(database, sql, 1, ct);
            return result.Success
                ? ToolJson.Serialize(result.Rows.FirstOrDefault())
                : $"Error: {result.ErrorMessage}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
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
