using ModelContextProtocol.Server;
using PostgresMcpServer.Services;
using System.ComponentModel;
using System.Diagnostics;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public class BackupTools
{
    private readonly IConnectionManager _connectionManager;
    private readonly ISafetyGuard _safetyGuard;

    public BackupTools(IConnectionManager connectionManager, ISafetyGuard safetyGuard)
    {
        _connectionManager = connectionManager;
        _safetyGuard = safetyGuard;
    }

    [McpServerTool, Description("Create a database backup using pg_dump")]
    public async Task<string> Backup(
        [Description("Name of the database connection to use")] string database,
        [Description("Output file path for the backup")] string outputPath,
        [Description("Backup format: plain (SQL), custom, directory, or tar")] string format = "custom",
        [Description("Only backup schema (no data)")] bool schemaOnly = false,
        [Description("Only backup data (no schema)")] bool dataOnly = false,
        [Description("Specific tables to backup (comma-separated)")] string? tables = null,
        [Description("Set to true to preview the command without executing")] bool dryRun = false,
        [Description("Confirm execution")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found.";

        if (schemaOnly && dataOnly)
            return "Error: schemaOnly and dataOnly are mutually exclusive.";

        var conn = GetConnectionParts(database);
        if (conn is null)
            return "Error: Could not parse the connection string for this database.";

        var formatArg = format.ToLowerInvariant() switch
        {
            "plain" => "-Fp",
            "custom" => "-Fc",
            "directory" => "-Fd",
            "tar" => "-Ft",
            _ => "-Fc"
        };

        // Built as a token list rather than one concatenated string: a path or table name
        // containing a space would otherwise split into extra arguments, and a value starting
        // with '-' would be read as an option.
        var args = new List<string>
        {
            "-h", conn.Host,
            "-p", conn.Port,
            "-U", conn.Username,
            "-d", conn.Database,
            "-w",               // never prompt for a password; stdin is not a terminal here
            formatArg,
            "-f", outputPath
        };

        if (schemaOnly) args.Add("--schema-only");
        if (dataOnly) args.Add("--data-only");

        foreach (var table in SplitTables(tables))
        {
            args.Add("-t");
            args.Add(table);
        }

        var display = DisplayCommand("pg_dump", args, conn.Password);

        if (dryRun)
        {
            if (!_safetyGuard.Settings.EnableDryRun)
                return "Error: Dry-run previews are disabled by configuration (Safety.EnableDryRun=false).";
            return $"[DRY RUN] Would execute:\n\n{display}\n\n" +
                   "The password is supplied via the PGPASSWORD environment variable of the child process.";
        }

        if (!confirm)
        {
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                operation = "BACKUP",
                command = display,
                outputPath,
                format,
                message = "Set confirm=true to create the backup."
            });
        }

        try
        {
            var (exitCode, _, stdErr) = await RunAsync("pg_dump", args, conn, ct);

            if (exitCode != 0)
                return $"Error: pg_dump failed with exit code {exitCode}.\n{stdErr}";

            var fileInfo = new FileInfo(outputPath);
            return ToolJson.Serialize(new
            {
                success = true,
                outputPath,
                format,
                size = fileInfo.Exists ? $"{fileInfo.Length / 1024.0:F2} KB" : "unknown",
                warnings = string.IsNullOrWhiteSpace(stdErr) ? null : stdErr
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Error: pg_dump was not found. Install the PostgreSQL client tools and ensure pg_dump is in PATH.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Restore a database from a backup file using pg_restore")]
    public async Task<string> Restore(
        [Description("Name of the database connection to use")] string database,
        [Description("Path to the backup file")] string inputPath,
        [Description("Clean (drop) database objects before recreating")] bool clean = false,
        [Description("Create the database before restoring")] bool createDb = false,
        [Description("Set to true to preview the command without executing")] bool dryRun = false,
        [Description("Confirm this potentially destructive operation")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!_connectionManager.DatabaseExists(database))
            return $"Error: Database '{database}' not found.";

        if (!File.Exists(inputPath))
            return $"Error: Backup file not found: {inputPath}";

        var conn = GetConnectionParts(database);
        if (conn is null)
            return "Error: Could not parse the connection string for this database.";

        var args = new List<string>
        {
            "-h", conn.Host,
            "-p", conn.Port,
            "-U", conn.Username,
            "-d", conn.Database,
            "-w"
        };

        // Options precede the positional filename; appending them afterwards relies on
        // getopt argument permutation, which is not guaranteed on every platform.
        if (clean) args.Add("--clean");
        if (createDb) args.Add("--create");
        args.Add(inputPath);

        var display = DisplayCommand("pg_restore", args, conn.Password);

        if (dryRun)
        {
            if (!_safetyGuard.Settings.EnableDryRun)
                return "Error: Dry-run previews are disabled by configuration (Safety.EnableDryRun=false).";
            return $"[DRY RUN] Would execute:\n\n{display}\n\n" +
                   "The password is supplied via the PGPASSWORD environment variable of the child process.";
        }

        if (!confirm)
        {
            return ToolJson.Serialize(new
            {
                requiresConfirmation = true,
                riskLevel = clean ? "critical" : "high",
                operation = "RESTORE",
                warning = clean
                    ? "This DROPS existing objects before restoring them."
                    : "This restores data and may overwrite existing data.",
                command = display,
                inputPath,
                message = "Set confirm=true to restore the backup."
            });
        }

        try
        {
            var (exitCode, _, stdErr) = await RunAsync("pg_restore", args, conn, ct);

            // Exit code is the only reliable success signal. The previous check treated any
            // failure whose output mentioned "warning" as a success, and pg_restore emits
            // warnings alongside genuine errors routinely.
            if (exitCode != 0)
            {
                return ToolJson.Serialize(new
                {
                    success = false,
                    exitCode,
                    error = stdErr,
                    message = "pg_restore reported a failure. The database may be partially restored."
                });
            }

            return ToolJson.Serialize(new
            {
                success = true,
                inputPath,
                warnings = string.IsNullOrWhiteSpace(stdErr) ? null : stdErr
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Error: pg_restore was not found. Install the PostgreSQL client tools and ensure pg_restore is in PATH.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static IEnumerable<string> SplitTables(string? tables) =>
        string.IsNullOrWhiteSpace(tables)
            ? []
            : tables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Renders the command for display. The password is never on the command line, but any
    /// accidental occurrence is masked. Guards against the empty-string case, where
    /// string.Replace throws ArgumentException.
    /// </summary>
    private static string DisplayCommand(string exe, IEnumerable<string> args, string? password)
    {
        var rendered = $"{exe} {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
        return string.IsNullOrEmpty(password) ? rendered : rendered.Replace(password, "********");
    }

    /// <summary>
    /// Starts the child process, drains both pipes concurrently (an unread pipe eventually
    /// fills and deadlocks), and kills the process tree if the call is cancelled - a restore
    /// left running after cancellation keeps writing to the database.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IEnumerable<string> args, ConnectionParts conn, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        if (!string.IsNullOrEmpty(conn.Password)) psi.Environment["PGPASSWORD"] = conn.Password;
        if (!string.IsNullOrEmpty(conn.SslMode)) psi.Environment["PGSSLMODE"] = conn.SslMode;
        if (!string.IsNullOrEmpty(conn.RootCertificate)) psi.Environment["PGSSLROOTCERT"] = conn.RootCertificate;
        if (conn.TimeoutSeconds > 0) psi.Environment["PGCONNECT_TIMEOUT"] = conn.TimeoutSeconds.ToString();

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {fileName}.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PROCESS-KILL-FAILURE] Could not stop {fileName}: {ex.Message}");
            }
            throw;
        }

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private ConnectionParts? GetConnectionParts(string database)
    {
        try
        {
            var dataSource = _connectionManager.GetDataSource(database);
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

            return new ConnectionParts
            {
                Host = builder.Host ?? "localhost",
                Port = builder.Port.ToString(),
                Database = builder.Database ?? database,
                Username = builder.Username ?? "",
                Password = builder.Password,
                // Carried through so a TLS-required server does not silently reject the dump.
                SslMode = builder.SslMode.ToString().ToLowerInvariant() switch
                {
                    "disable" => "disable",
                    "allow" => "allow",
                    "prefer" => "prefer",
                    "require" => "require",
                    "verifyca" => "verify-ca",
                    "verifyfull" => "verify-full",
                    _ => null
                },
                RootCertificate = builder.RootCertificate,
                TimeoutSeconds = builder.Timeout
            };
        }
        catch
        {
            return null;
        }
    }

    private class ConnectionParts
    {
        public string Host { get; set; } = "localhost";
        public string Port { get; set; } = "5432";
        public string Database { get; set; } = "";
        public string Username { get; set; } = "";
        public string? Password { get; set; }
        public string? SslMode { get; set; }
        public string? RootCertificate { get; set; }
        public int TimeoutSeconds { get; set; }
    }
}
