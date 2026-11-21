using Npgsql;

namespace PostgresMcpServer.Services;

/// <param name="Target">host:port/database as user, or empty when the value did not parse.</param>
/// <param name="Message">What the server reported on success, or the first line of the error.</param>
public sealed record ProbeResult(bool Success, string Target, string Message);

/// <summary>
/// Opens a real connection, so a setup problem is diagnosed from a terminal rather than
/// through an MCP client that reports it as "server failed to start".
/// </summary>
public static class ConnectionProbe
{
    public static async Task<ProbeResult> ProbeAsync(string? connection, CancellationToken ct = default)
    {
        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = ConnectionStrings.Parse(connection);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, string.Empty, $"Connection string does not parse: {ex.Message}");
        }

        var target = $"{parsed.Host}:{parsed.Port}/{parsed.Database} as {parsed.Username}";

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(parsed.ConnectionString);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand(
                "SELECT current_database(), current_user, version()", conn);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);

            var serverVersion = reader.GetString(2).Split(" on ")[0];

            return new ProbeResult(true, target,
                $"Connected as {reader.GetString(1)} to {reader.GetString(0)} - {serverVersion}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, target, ex.Message.Split('\n')[0]);
        }
    }
}
