using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;

var builder = Host.CreateApplicationBuilder(args);

// Get the directory where the executable is located
var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;

// Load configuration from executable directory
builder.Configuration
    .AddJsonFile(Path.Combine(exeDir, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(Path.Combine(exeDir, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true)
    .AddEnvironmentVariables("POSTGRES_MCP_");

// Register configuration
builder.Services.Configure<DatabaseConfig>(builder.Configuration);

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
