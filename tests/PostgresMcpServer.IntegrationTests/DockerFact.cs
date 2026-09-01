using System.Diagnostics;

namespace PostgresMcpServer.IntegrationTests;

/// <summary>
/// Detects a reachable Docker daemon once per test run. The CLI can be installed while the
/// engine is stopped, so presence of the executable is not sufficient.
/// </summary>
public static class DockerAvailability
{
    public static bool IsAvailable => Probe.Value;

    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "info", "--format", "{{.ServerVersion}}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null) return false;
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });
}

/// <summary>A [Fact] that is skipped, rather than failed, when no Docker daemon is reachable.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
            Skip = "Docker is not available on this machine; PostgreSQL integration tests were skipped.";
    }
}
