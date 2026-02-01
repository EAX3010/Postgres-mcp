using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;

// Get the directory where the executable is located
var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;

// Build configuration from appsettings.json
var configuration = new ConfigurationBuilder()
    .SetBasePath(exeDir)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var builder = Host.CreateApplicationBuilder(args);

// Clear default configuration and use ours
builder.Configuration.Sources.Clear();
builder.Configuration.AddConfiguration(configuration);

// Register configuration
builder.Services.Configure<DatabaseConfig>(configuration);

// Register services
builder.Services.AddSingleton<IConnectionManager, ConnectionManager>();
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();
builder.Services.AddSingleton<ISafetyGuard, SafetyGuard>();
builder.Services.AddSingleton<IPostgresService, PostgresService>();

// Configure MCP Server with stdio transport
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

await app.RunAsync();
