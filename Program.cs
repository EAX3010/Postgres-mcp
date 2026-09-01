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

        Provide at least one connection through either source:

          1. A file next to the executable:
                 {Path.Combine(baseDir, "appsettings.json")}
             Copy appsettings.example.json to appsettings.json and edit the Databases section.

          2. The environment, with no file required:
                 POSTGRESMCP_Databases__local=Host=localhost;Port=5432;Database=mydb;Username=me;Password=...
             Use __ (double underscore) as the section separator.

        Run with --check to diagnose, or --help for usage.
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
        Console.WriteLine("Add a Databases entry to appsettings.json, or set");
        Console.WriteLine("POSTGRESMCP_Databases__<name> in the environment.");
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
      PostgresMcpServer --check         Validate configuration and connect to every database
      PostgresMcpServer --version       Print the version
      PostgresMcpServer --help          Show this help

    CONFIGURATION
      Settings are read from an optional file next to the executable:
          {Path.Combine(baseDir, "appsettings.json")}
      and from POSTGRESMCP_-prefixed environment variables, which take precedence.
      Either source alone is sufficient; the server needs at least one database.

        POSTGRESMCP_Databases__local=Host=localhost;Port=5432;Database=mydb;Username=me;Password=...

      Use __ (double underscore) as the section separator.

    NOTE
      Running this without an MCP client is only useful with --check. Started bare, it waits
      for JSON-RPC on stdin and writes protocol messages to stdout.

    Docs: docs/README.md
    """;
