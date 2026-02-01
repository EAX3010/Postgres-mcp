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
}

public class ConnectionManager : IConnectionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _dataSources = new();
    private readonly DatabaseConfig _config;

    public ConnectionManager(IOptions<DatabaseConfig> config)
    {
        _config = config.Value;
        InitializeDataSources();
    }

    private void InitializeDataSources()
    {
        foreach (var (name, connectionString) in _config.Databases)
        {
            var dataSource = NpgsqlDataSource.Create(connectionString);
            _dataSources[name] = dataSource;
        }
    }

    public NpgsqlDataSource GetDataSource(string databaseName)
    {
        if (_dataSources.TryGetValue(databaseName, out var dataSource))
        {
            return dataSource;
        }
        throw new ArgumentException($"Database '{databaseName}' not found in configuration. Available: {string.Join(", ", _dataSources.Keys)}");
    }

    public IEnumerable<string> GetDatabaseNames() => _dataSources.Keys;

    public bool DatabaseExists(string databaseName) => _dataSources.ContainsKey(databaseName);

    public void Dispose()
    {
        foreach (var dataSource in _dataSources.Values)
        {
            dataSource.Dispose();
        }
        _dataSources.Clear();
    }
}
