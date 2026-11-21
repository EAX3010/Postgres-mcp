using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;
using PostgresMcpServer.Setup;
using System.Text;

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

var (configuration, databaseUrl) = ConfigurationLoader.Load(
    baseDir, Environment.GetEnvironmentVariable(ConfigurationLoader.DatabaseUrlVariable));

var config = configuration.Get<DatabaseConfig>() ?? new DatabaseConfig();

if (args.Contains("--check") || args.Contains("--doctor"))
    return await RunDiagnosticsAsync(baseDir, config, databaseUrl);

// An MCP client always talks to this process through pipes. A console on both ends means a
// person started it - by double-clicking it, or by typing its name - and waiting silently for
// JSON-RPC would look like a hang, so that person gets the guided setup instead.
var startedByAPerson = args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected;

if (args.Contains("--setup") || startedByAPerson)
{
    var result = await RunSetupAsync(baseDir, config);

    // A double-clicked console window closes the moment the process exits.
    if (startedByAPerson)
    {
        Console.WriteLine();
        Console.Write("Press Enter to close.");
        Console.ReadLine();
    }

    return result;
}

// Only a total absence of databases is fatal; a single bad entry is reported and skipped.
if (config.Databases.Count == 0)
{
    Console.Error.WriteLine($"""
        [FATAL] No databases are configured.

        Run the guided setup, which asks for the connection and tests it:

            {ExecutableHint()} --setup

        Alternatively supply the connection through the environment, with no file:

            DATABASE_URL=postgres://me:password@localhost:5432/mydb

        --help lists the other options.
        """);
    return 1;
}

if (databaseUrl == DatabaseUrlStatus.Ignored)
{
    Console.Error.WriteLine(
        "[CONFIG] DATABASE_URL is set but ignored, because databases are configured explicitly.");
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
/// Writes a minimal appsettings.json next to the executable, for editing by hand. A downloaded
/// release contains no repository, so the binary has to produce its own starting configuration.
/// </summary>
static int RunInit(string baseDir, bool force)
{
    var configPath = Path.Combine(baseDir, ConfigurationLoader.FileName);

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

    StarterConfiguration.WriteSettings(configPath, StarterConfiguration.Json);

    Console.WriteLine($"""
        Created {configPath}

        Next steps:

          1. Open that file and replace CHANGE_ME with your PostgreSQL password.
             Adjust Host, Port, Database and Username to match your server.
             Every option is documented in appsettings.example.json beside it.

          2. Verify the connection:
                 {ExecutableHint()} --check

          3. Register the server with your AI client:
                 {ExecutableHint()} --setup

             To do that by hand instead, the path to give the client is:
                 {ExecutablePath(baseDir)}

        Full documentation: https://github.com/EAX3010/Postgres-mcp
        """);

    return 0;
}

static async Task<int> RunSetupAsync(string baseDir, DatabaseConfig config)
{
    var wizard = new SetupWizard(Console.In, Console.Out, new SetupOptions
    {
        BaseDirectory = baseDir,
        ExecutablePath = ExecutablePath(baseDir),
        Paths = SetupPaths.Current(),
        ExistingDatabases = config.Databases,
        // Piped input has no keys to intercept; the wizard reads a plain line instead.
        ReadSecret = Console.IsInputRedirected ? null : ReadSecret
    });

    return await wizard.RunAsync();
}

/// <summary>Reads a password from the keyboard, showing an asterisk per character.</summary>
static string? ReadSecret()
{
    var secret = new StringBuilder();

    while (true)
    {
        var key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return secret.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (secret.Length == 0) continue;
            secret.Length--;
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            secret.Append(key.KeyChar);
            Console.Write('*');
        }
    }
}

static string ExecutableName() =>
    OperatingSystem.IsWindows() ? "PostgresMcpServer.exe" : "PostgresMcpServer";

static string ExecutableHint() =>
    OperatingSystem.IsWindows() ? "PostgresMcpServer.exe" : "./PostgresMcpServer";

static string ExecutablePath(string baseDir) => Path.Combine(baseDir, ExecutableName());

/// <summary>
/// Validates configuration and actually connects to every database, so setup problems can be
/// diagnosed from a terminal instead of through an MCP client that reports them as tool errors.
/// </summary>
static async Task<int> RunDiagnosticsAsync(string baseDir, DatabaseConfig config, DatabaseUrlStatus databaseUrl)
{
    var configPath = Path.Combine(baseDir, ConfigurationLoader.FileName);

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

    if (databaseUrl == DatabaseUrlStatus.Used)
        Console.WriteLine($"DATABASE_URL          : used, as the database named '{ConfigurationLoader.DatabaseUrlName}'");
    else if (databaseUrl == DatabaseUrlStatus.Ignored)
        Console.WriteLine("DATABASE_URL          : set but ignored - databases are configured explicitly");

    Console.WriteLine();

    if (config.Databases.Count == 0)
    {
        Console.WriteLine("FAIL  No databases are configured.");
        Console.WriteLine();
        Console.WriteLine($"Run '{ExecutableHint()} --setup' to connect one,");
        Console.WriteLine("or set DATABASE_URL in the environment.");
        return 1;
    }

    Console.WriteLine($"Databases ({config.Databases.Count}):");
    Console.WriteLine();

    var failures = 0;

    foreach (var (name, connectionString) in config.Databases)
    {
        var probe = await ConnectionProbe.ProbeAsync(connectionString);
        if (!probe.Success) failures++;

        Console.WriteLine($"  {(probe.Success ? "[ OK ]" : "[FAIL]")} {name}");
        if (probe.Target.Length > 0) Console.WriteLine($"         {probe.Target}");
        Console.WriteLine($"         {probe.Message}");
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
      PostgresMcpServer --setup         Guided setup: connect a database, register with your AI client
      PostgresMcpServer --check         Validate configuration and connect to every database
      PostgresMcpServer --init          Create a starter appsettings.json to edit by hand
      PostgresMcpServer --init --force  Overwrite an existing appsettings.json
      PostgresMcpServer --version       Print the version
      PostgresMcpServer --help          Show this help
      PostgresMcpServer                 Started by an AI client: run as an MCP server over stdio
                                        Started from a terminal: the same as --setup
      PostgresMcpServer --stdio         Run as an MCP server even when started from a terminal

    FIRST RUN
      {ExecutableHint()} --setup

    CONFIGURATION
      Settings are read from an optional file next to the executable:
          {Path.Combine(baseDir, ConfigurationLoader.FileName)}
      and from POSTGRESMCP_-prefixed environment variables, which take precedence.
      Either source alone is sufficient; the server needs at least one database.

        POSTGRESMCP_Databases__local=postgres://me:password@localhost:5432/mydb

      Use __ (double underscore) as the section separator. A connection is either a
      postgres:// URL or an Npgsql keyword string (Host=...;Database=...).

      When no database is configured either way, DATABASE_URL is used if it is set,
      under the name '{ConfigurationLoader.DatabaseUrlName}'.

    Docs: https://github.com/EAX3010/Postgres-mcp
    """;
