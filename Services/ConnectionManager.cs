using Microsoft.Extensions.Options;
using Npgsql;
using PostgresMcpServer.Models;
using System.Collections.Concurrent;

namespace PostgresMcpServer.Services;

public interface IConnectionManager
{
    NpgsqlDataSource GetDataSource(string databaseName);
    IEnumerable<string> GetDatabaseNames();
    bool DatabaseExists(string databaseName);

    /// <summary>Connection strings that failed to parse at startup, keyed by configured name.</summary>
    IReadOnlyDictionary<string, string> ConfigurationErrors { get; }
}

public class ConnectionManager : IConnectionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _dataSources = new();
    private readonly Dictionary<string, string> _configurationErrors = [];
    private readonly DatabaseConfig _config;

    public ConnectionManager(IOptions<DatabaseConfig> config)
    {
        _config = config.Value;
        InitializeDataSources();
    }

    public IReadOnlyDictionary<string, string> ConfigurationErrors => _configurationErrors;

    /// <summary>
    /// One malformed connection string used to abort startup with a raw exception, which is
    /// invisible over a stdio transport. Each entry is now isolated and reported instead.
    /// </summary>
    private void InitializeDataSources()
    {
        foreach (var (name, connectionString) in _config.Databases)
        {
            try
            {
                _dataSources[name] = NpgsqlDataSource.Create(connectionString);
            }
            catch (Exception ex)
            {
                _configurationErrors[name] = ex.Message;
                Console.Error.WriteLine(
                    $"[CONFIG-ERROR] Database '{name}' was skipped: {ex.Message}");
            }
        }

        if (_dataSources.IsEmpty)
        {
            Console.Error.WriteLine(
                "[CONFIG-WARNING] No usable database connections were configured. " +
                "Check the 'Databases' section of appsettings.json.");
        }
    }

    public NpgsqlDataSource GetDataSource(string databaseName)
    {
        if (_dataSources.TryGetValue(databaseName, out var dataSource))
            return dataSource;

        if (_configurationErrors.TryGetValue(databaseName, out var error))
            throw new ArgumentException($"Database '{databaseName}' is configured but its connection string is invalid: {error}");

        throw new ArgumentException(
            $"Database '{databaseName}' not found in configuration. Available: {string.Join(", ", _dataSources.Keys)}");
    }

    public IEnumerable<string> GetDatabaseNames() => _dataSources.Keys;

    public bool DatabaseExists(string databaseName) => _dataSources.ContainsKey(databaseName);

    public void Dispose()
    {
        foreach (var dataSource in _dataSources.Values)
            dataSource.Dispose();

        _dataSources.Clear();
        GC.SuppressFinalize(this);
    }
}
