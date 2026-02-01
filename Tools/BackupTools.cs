using ModelContextProtocol.Server;
using PostgresMcpServer.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PostgresMcpServer.Tools;

[McpServerToolType]
public class BackupTools
{
    private readonly IConnectionManager _connectionManager;

    public BackupTools(IConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
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
        {
            return $"Error: Database '{database}' not found.";
        }

        // Parse connection string to get pg_dump parameters
        var connString = GetConnectionStringParts(database);
        if (connString == null)
        {
            return "Error: Could not parse connection string.";
        }

        var formatArg = format.ToLowerInvariant() switch
        {
            "plain" => "-Fp",
            "custom" => "-Fc",
            "directory" => "-Fd",
            "tar" => "-Ft",
            _ => "-Fc"
        };

        var args = new List<string>
        {
            $"-h {connString.Host}",
            $"-p {connString.Port}",
            $"-U {connString.Username}",
            $"-d {connString.Database}",
            formatArg,
            $"-f \"{outputPath}\""
        };

        if (schemaOnly) args.Add("--schema-only");
        if (dataOnly) args.Add("--data-only");

        if (!string.IsNullOrEmpty(tables))
        {
            foreach (var table in tables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                args.Add($"-t {table}");
            }
        }

        var command = $"pg_dump {string.Join(" ", args)}";

        if (dryRun)
        {
            return $"[DRY RUN] Would execute:\n\n{command}\n\nNote: Requires PGPASSWORD environment variable or .pgpass file for authentication.";
        }

        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                requiresConfirmation = true,
                operation = "BACKUP",
                command = command.Replace(connString.Password ?? "", "********"),
                outputPath,
                format,
                message = "Set confirm=true to create the backup."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pg_dump",
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrEmpty(connString.Password))
            {
                psi.Environment["PGPASSWORD"] = connString.Password;
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                return "Error: Failed to start pg_dump process.";
            }

            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                return $"Error: pg_dump failed with exit code {process.ExitCode}.\n{error}";
            }

            var fileInfo = new FileInfo(outputPath);
            return JsonSerializer.Serialize(new
            {
                success = true,
                outputPath,
                format,
                size = fileInfo.Exists ? $"{fileInfo.Length / 1024.0:F2} KB" : "unknown"
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}\n\nMake sure pg_dump is installed and in your PATH.";
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
        {
            return $"Error: Database '{database}' not found.";
        }

        if (!File.Exists(inputPath))
        {
            return $"Error: Backup file not found: {inputPath}";
        }

        var connString = GetConnectionStringParts(database);
        if (connString == null)
        {
            return "Error: Could not parse connection string.";
        }

        var args = new List<string>
        {
            $"-h {connString.Host}",
            $"-p {connString.Port}",
            $"-U {connString.Username}",
            $"-d {connString.Database}",
            $"\"{inputPath}\""
        };

        if (clean) args.Add("--clean");
        if (createDb) args.Add("--create");

        var command = $"pg_restore {string.Join(" ", args)}";

        if (dryRun)
        {
            return $"[DRY RUN] Would execute:\n\n{command}\n\nNote: Requires PGPASSWORD environment variable or .pgpass file for authentication.";
        }

        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                requiresConfirmation = true,
                riskLevel = clean ? "HIGH" : "MEDIUM",
                operation = "RESTORE",
                warning = clean ? "This will DROP existing objects before restoring!" : "This will restore data which may overwrite existing data.",
                command = command.Replace(connString.Password ?? "", "********"),
                inputPath,
                message = "Set confirm=true to restore the backup."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pg_restore",
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrEmpty(connString.Password))
            {
                psi.Environment["PGPASSWORD"] = connString.Password;
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                return "Error: Failed to start pg_restore process.";
            }

            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            // pg_restore often returns warnings that aren't fatal
            if (process.ExitCode != 0 && !error.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                return $"Error: pg_restore failed with exit code {process.ExitCode}.\n{error}";
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                inputPath,
                warnings = string.IsNullOrEmpty(error) ? null : error
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}\n\nMake sure pg_restore is installed and in your PATH.";
        }
    }

    private ConnectionParts? GetConnectionStringParts(string database)
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
                Password = builder.Password
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
    }
}
