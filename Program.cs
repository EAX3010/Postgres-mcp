using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;

// AppContext.BaseDirectory is the application directory and, unlike Assembly.Location,
// is still correct for a single-file publish.
var baseDir = AppContext.BaseDirectory;
var version = typeof(DatabaseConfig).Assembly.GetName().Version?.ToString() ?? "unknown";

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(HelpText(baseDir));
    return 0;
}

if (args.Contains("--version"))
{
    Console.WriteLine($"PostgresMcpServer {version}");
    return 0;
}

if (args.Contains("--init"))
    return RunInit(baseDir, force: args.Contains("--force"));

// Configuration comes from an optional file next to the executable plus POSTGRESMCP_*
// environment variables, so a deployment can use either, both, or only the environment.
var configuration = new ConfigurationBuilder()
    .SetBasePath(baseDir)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("POSTGRESMCP_")
    .Build();

var config = configuration.Get<DatabaseConfig>() ?? new DatabaseConfig();

if (args.Contains("--check") || args.Contains("--doctor"))
    return await RunDiagnosticsAsync(baseDir, config);

// Only a total absence of databases is fatal; a single bad entry is reported and skipped.
if (config.Databases.Count == 0)
{
    Console.Error.WriteLine($"""
        [FATAL] No databases are configured.

        Run this to create a starter configuration file:

            {ExecutableHint()} --init

        Then edit {Path.Combine(baseDir, "appsettings.json")} and run --check.

        Alternatively supply everything through the environment, with no file:

            POSTGRESMCP_Databases__local=Host=localhost;Port=5432;Database=mydb;Username=me;Password=...

        Use __ (double underscore) as the section separator. --help for more.
        """);
    return 1;
}

// An empty argument array is passed deliberately: the host's command-line configuration
// provider rejects bare flags such as --check, and this server takes its settings from the
// file and environment instead.
var builder = Host.CreateApplicationBuilder([]);

builder.Configuration.Sources.Clear();
builder.Configuration.AddConfiguration(configuration);

// stdout is the MCP JSON-RPC channel. Anything else written there corrupts the protocol
// stream, and Host.CreateApplicationBuilder installs a console logger that targets stdout
// by default, so every log record is redirected to stderr here.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.Configure<DatabaseConfig>(builder.Configuration);

builder.Services.AddSingleton<IConnectionManager, ConnectionManager>();
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();
builder.Services.AddSingleton<ISafetyGuard, SafetyGuard>();
builder.Services.AddSingleton<IPostgresService, PostgresService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

await app.RunAsync();
return 0;

/// <summary>
/// Writes a minimal appsettings.json next to the executable. A downloaded release contains no
/// repository, so the binary has to be able to produce its own starting configuration.
/// </summary>
static int RunInit(string baseDir, bool force)
{
    var configPath = Path.Combine(baseDir, "appsettings.json");

    if (File.Exists(configPath) && !force)
    {
        Console.WriteLine($"""
            A configuration file already exists:
                {configPath}

            It was left untouched. Pass --force to overwrite it, or edit it directly.
            Run --check to test the connections it describes.
            """);
        return 0;
    }

    File.WriteAllText(configPath, StarterConfig());

    Console.WriteLine($"""
        Created {configPath}

        Next steps:

          1. Open that file and replace CHANGE_ME with your PostgreSQL password.
             Adjust Host, Port, Database and Username to match your server.

          2. Verify the connection:
                 {ExecutableHint()} --check

          3. Register the server with your MCP client. The path to give it is:
                 {Path.Combine(baseDir, ExecutableName())}

             Claude Desktop  %APPDATA%\Claude\claude_desktop_config.json (Windows)
                             ~/Library/Application Support/Claude/claude_desktop_config.json (macOS)
             Claude Code     claude mcp add postgres --scope user "<path above>"
             Cursor          ~/.cursor/mcp.json
             VS Code         .vscode/mcp.json          (uses "servers", not "mcpServers")
             Windsurf        ~/.codeium/windsurf/mcp_config.json
             Zed             settings.json             (uses "context_servers")

             Ready-to-copy files for each client are in the examples folder beside
             this executable, and every option is documented in appsettings.example.json.

        Full documentation: https://github.com/yourusername/postgres-mcp-server
        """);

    return 0;
}

static string ExecutableName() =>
    OperatingSystem.IsWindows() ? "PostgresMcpServer.exe" : "PostgresMcpServer";

static string ExecutableHint() =>
    OperatingSystem.IsWindows() ? "PostgresMcpServer.exe" : "./PostgresMcpServer";

/// <summary>
/// Validates configuration and actually connects to every database, so setup problems can be
/// diagnosed from a terminal instead of through an MCP client that reports them as tool errors.
/// </summary>
static async Task<int> RunDiagnosticsAsync(string baseDir, DatabaseConfig config)
{
    var configPath = Path.Combine(baseDir, "appsettings.json");

    Console.WriteLine("PostgresMcpServer configuration check");
    Console.WriteLine(new string('-', 60));
    Console.WriteLine($"Application directory : {baseDir}");
    Console.WriteLine($"Config file           : {(File.Exists(configPath) ? configPath : "(not present - using environment only)")}");

    var auditPath = Path.IsPathRooted(config.Audit.LogPath)
        ? config.Audit.LogPath
        : Path.Combine(baseDir, config.Audit.LogPath);
    Console.WriteLine($"Audit log             : {(config.Audit.Enabled ? auditPath : "(disabled)")}");
    Console.WriteLine($"Confirm at risk level : {config.Safety.ConfirmAtRiskLevel}");
    Console.WriteLine($"Multi-statement SQL   : {(config.Safety.AllowMultiStatement ? "ALLOWED (not recommended)" : "refused")}");
    Console.WriteLine($"Max rows / response   : {config.Limits.MaxRows} rows / {config.Limits.MaxResponseBytes:N0} bytes");
    Console.WriteLine();

    if (config.Databases.Count == 0)
    {
        Console.WriteLine("FAIL  No databases are configured.");
        Console.WriteLine();
        Console.WriteLine($"Run '{ExecutableHint()} --init' to create a starter configuration file,");
        Console.WriteLine("or set POSTGRESMCP_Databases__<name> in the environment.");
        return 1;
    }

    Console.WriteLine($"Databases ({config.Databases.Count}):");
    Console.WriteLine();

    var failures = 0;

    foreach (var (name, connectionString) in config.Databases)
    {
        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"         Connection string does not parse: {ex.Message}");
            Console.WriteLine();
            continue;
        }

        var target = $"{parsed.Host}:{parsed.Port}/{parsed.Database} as {parsed.Username}";

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT current_database(), current_user, version()", connection);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();

            var serverVersion = reader.GetString(2).Split(" on ")[0];

            Console.WriteLine($"  [ OK ] {name}");
            Console.WriteLine($"         {target}");
            Console.WriteLine($"         Connected as {reader.GetString(1)} to {reader.GetString(0)} - {serverVersion}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"         {target}");
            Console.WriteLine($"         {ex.Message.Split('\n')[0]}");
        }

        Console.WriteLine();
    }

    if (failures == 0)
    {
        Console.WriteLine("All databases reachable. The server is ready to register with an MCP client.");
        return 0;
    }

    Console.WriteLine($"{failures} of {config.Databases.Count} database(s) failed. See docs/troubleshooting.md.");
    return 1;
}

static string HelpText(string baseDir) => $"""
    PostgresMcpServer - a Model Context Protocol server for PostgreSQL.

    USAGE
      PostgresMcpServer                 Run as an MCP server over stdio (how MCP clients start it)
      PostgresMcpServer --init          Create a starter appsettings.json next to the executable
      PostgresMcpServer --init --force  Overwrite an existing appsettings.json
      PostgresMcpServer --check         Validate configuration and connect to every database
      PostgresMcpServer --version       Print the version
      PostgresMcpServer --help          Show this help

    FIRST RUN
      1. {ExecutableHint()} --init
      2. Edit appsettings.json and set your password
      3. {ExecutableHint()} --check
      4. Register the executable with your MCP client (see the examples folder)

    CONFIGURATION
      Settings are read from an optional file next to the executable:
          {Path.Combine(baseDir, "appsettings.json")}
      and from POSTGRESMCP_-prefixed environment variables, which take precedence.
      Either source alone is sufficient; the server needs at least one database.

        POSTGRESMCP_Databases__local=Host=localhost;Port=5432;Database=mydb;Username=me;Password=...

      Use __ (double underscore) as the section separator.

    NOTE
      Running this without an MCP client is only useful with --init or --check. Started bare,
      it waits for JSON-RPC on stdin and writes protocol messages to stdout.

    Docs: docs/README.md
    """;

/// <summary>
/// The minimal starting configuration. Deliberately smaller than appsettings.example.json,
/// which documents every option across several connections and would fail --check repeatedly.
/// </summary>
static string StarterConfig() => """
    {
      "Databases": {
        "local": "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=CHANGE_ME"
      },
      "Safety": {
        "RequireConfirmation": true,
        "EnableDryRun": true,
        "ConfirmAtRiskLevel": "High",
        "AllowMultiStatement": false
      },
      "Limits": {
        "CommandTimeoutSeconds": 30,
        "MaxRows": 1000,
        "MaxResponseBytes": 1000000
      },
      "Audit": {
        "Enabled": true,
        "LogPath": "audit.log",
        "LogToConsole": false
      }
    }

    """;
