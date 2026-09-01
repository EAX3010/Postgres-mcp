namespace PostgresMcpServer.Models;

public class DatabaseConfig
{
    public Dictionary<string, string> Databases { get; set; } = new();
    public SafetySettings Safety { get; set; } = new();
    public AuditSettings Audit { get; set; } = new();
    public LimitSettings Limits { get; set; } = new();
}

public class SafetySettings
{
    public static readonly string[] DefaultCriticalOperations =
        ["DROP", "TRUNCATE", "DELETE", "ALTER", "GRANT", "REVOKE"];

    public bool RequireConfirmation { get; set; } = true;

    /// <summary>
    /// When true, write tools refuse to run unless the caller passes dryRun=false explicitly
    /// having first seen a dry-run preview. Honoured by the execute/admin/backup tools.
    /// </summary>
    public bool EnableDryRun { get; set; } = true;

    /// <summary>
    /// Deliberately empty by default: the configuration binder ADDS to a pre-populated
    /// collection rather than replacing it, which silently duplicated every entry.
    /// Defaults are applied by <see cref="EffectiveCriticalOperations"/> instead.
    /// </summary>
    public List<string> CriticalOperations { get; set; } = [];

    /// <summary>Minimum risk level that forces an explicit confirm=true.</summary>
    public RiskLevel ConfirmAtRiskLevel { get; set; } = RiskLevel.High;

    /// <summary>Multi-statement SQL is rejected outright; it defeats every per-statement check.</summary>
    public bool AllowMultiStatement { get; set; }

    public IReadOnlyList<string> EffectiveCriticalOperations =>
        (CriticalOperations.Count > 0 ? CriticalOperations : (IEnumerable<string>)DefaultCriticalOperations)
            .Select(o => o.Trim().ToUpperInvariant())
            .Where(o => o.Length > 0)
            .Distinct()
            .ToList();
}

public class AuditSettings
{
    public bool Enabled { get; set; } = true;
    public string LogPath { get; set; } = "audit.log";
    public bool LogToConsole { get; set; }
}

public class LimitSettings
{
    /// <summary>Per-command timeout. 0 disables it (Npgsql default is 30s).</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Hard ceiling on rows returned by the query tool, regardless of the caller's limit.</summary>
    public int MaxRows { get; set; } = 1000;

    /// <summary>Approximate ceiling on the serialized response, to avoid exhausting memory.</summary>
    public int MaxResponseBytes { get; set; } = 1_000_000;
}

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public static class RiskLevelExtensions
{
    public static string ToWireString(this RiskLevel level) => level.ToString().ToLowerInvariant();
}
