using Microsoft.Extensions.Options;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;
using Testcontainers.PostgreSql;

namespace PostgresMcpServer.IntegrationTests;

/// <summary>
/// Starts a disposable PostgreSQL instance for the suite. When Docker is unavailable the
/// fixture initialises to a no-op and every test is skipped by <see cref="DockerFactAttribute"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string DatabaseName = "test";

    private PostgreSqlContainer? _container;
    private string? _auditDirectory;

    public string ConnectionString { get; private set; } = string.Empty;
    public string AuditLogPath { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("mcp_test")
            .WithUsername("mcp")
            .WithPassword("mcp_password")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        _auditDirectory = Path.Combine(Path.GetTempPath(), "pgmcp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_auditDirectory);
        AuditLogPath = Path.Combine(_auditDirectory, "audit.log");
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();

        try
        {
            if (_auditDirectory is not null && Directory.Exists(_auditDirectory))
                Directory.Delete(_auditDirectory, recursive: true);
        }
        catch
        {
            // Temp cleanup only.
        }
    }

    public DatabaseConfig BuildConfig(Action<DatabaseConfig>? configure = null)
    {
        var config = new DatabaseConfig
        {
            Databases = { [DatabaseName] = ConnectionString },
            Audit = { Enabled = true, LogPath = AuditLogPath, LogToConsole = false }
        };

        configure?.Invoke(config);
        return config;
    }

    public (PostgresService Postgres, ConnectionManager Connections, AuditLogger Audit, SafetyGuard Guard)
        BuildServices(Action<DatabaseConfig>? configure = null)
    {
        var options = Options.Create(BuildConfig(configure));
        var connections = new ConnectionManager(options);
        var audit = new AuditLogger(options);
        var guard = new SafetyGuard(options);
        var postgres = new PostgresService(connections, audit, options);

        return (postgres, connections, audit, guard);
    }

    public string ReadAuditLog() => File.Exists(AuditLogPath) ? File.ReadAllText(AuditLogPath) : string.Empty;
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
