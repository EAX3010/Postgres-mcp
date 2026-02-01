using Microsoft.Extensions.Options;
using PostgresMcpServer.Models;
using System.Text;

namespace PostgresMcpServer.Services;

public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry);
    string InstanceId { get; }
}

public class AuditLogger : IAuditLogger
{
    private readonly AuditSettings _settings;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Unique identifier for this MCP server instance.
    /// Useful for distinguishing logs when multiple Claude instances are running.
    /// </summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];

    public AuditLogger(IOptions<DatabaseConfig> config)
    {
        _settings = config.Value.Audit;
    }

    public async Task LogAsync(AuditEntry entry)
    {
        if (!_settings.Enabled) return;

        // Add instance ID to the entry for multi-instance tracking
        entry.InstanceId = InstanceId;

        var logLine = entry.ToJson();

        if (_settings.LogToConsole)
        {
            Console.Error.WriteLine($"[AUDIT:{InstanceId}] {logLine}");
        }

        // Use file-level locking for multi-process safety
        await _writeLock.WaitAsync();
        try
        {
            await WriteWithFileLockAsync(_settings.LogPath, logLine + Environment.NewLine);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes to file with exclusive lock to handle multiple MCP server instances.
    /// </summary>
    private static async Task WriteWithFileLockAsync(string path, string content)
    {
        const int maxRetries = 5;
        const int retryDelayMs = 100;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // FileShare.Read allows other processes to read while we write
                // FileMode.Append ensures we add to end of file
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var bytes = Encoding.UTF8.GetBytes(content);
                await stream.WriteAsync(bytes);
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                // File might be locked by another instance, retry after delay
                await Task.Delay(retryDelayMs * (attempt + 1));
            }
        }
    }
}
