using Microsoft.Extensions.Options;
using PostgresMcpServer.Models;
using System.Text;

namespace PostgresMcpServer.Services;

public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry);
    string InstanceId { get; }
    string ResolvedLogPath { get; }
}

public class AuditLogger : IAuditLogger
{
    private readonly AuditSettings _settings;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _reportedFailure;

    /// <summary>
    /// Unique identifier for this MCP server instance, so logs from concurrent instances
    /// can be told apart.
    /// </summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Absolute log path. A relative LogPath is resolved against the application directory
    /// rather than the current working directory, which the host process controls and which
    /// otherwise silently splits the log across locations.
    /// </summary>
    public string ResolvedLogPath { get; }

    public AuditLogger(IOptions<DatabaseConfig> config)
    {
        _settings = config.Value.Audit;
        ResolvedLogPath = Path.IsPathRooted(_settings.LogPath)
            ? _settings.LogPath
            : Path.Combine(AppContext.BaseDirectory, _settings.LogPath);
    }

    public async Task LogAsync(AuditEntry entry)
    {
        if (!_settings.Enabled) return;

        entry.InstanceId = InstanceId;
        var logLine = entry.ToJson();

        if (_settings.LogToConsole)
            Console.Error.WriteLine($"[AUDIT:{InstanceId}] {logLine}");

        await _writeLock.WaitAsync();
        try
        {
            await WriteWithFileLockAsync(ResolvedLogPath, logLine + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Auditing must not take down an operation that already ran. Report once, then
            // stay quiet so a broken log path cannot flood stderr.
            if (!_reportedFailure)
            {
                _reportedFailure = true;
                Console.Error.WriteLine(
                    $"[AUDIT-FAILURE] Cannot write '{ResolvedLogPath}': {ex.Message}. " +
                    "Auditing is degraded for this session; further failures are suppressed.");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Appends with a share mode that tolerates concurrent readers, retrying briefly when a
    /// second server instance holds the file.
    /// </summary>
    private static async Task WriteWithFileLockAsync(string path, string content)
    {
        const int maxRetries = 5;
        const int retryDelayMs = 100;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var bytes = Encoding.UTF8.GetBytes(content);

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);

                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                await Task.Delay(retryDelayMs * (attempt + 1));
            }
        }
    }
}
