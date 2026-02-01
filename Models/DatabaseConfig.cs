namespace PostgresMcpServer.Models;

public class DatabaseConfig
{
    public Dictionary<string, string> Databases { get; set; } = new();
    public SafetySettings Safety { get; set; } = new();
    public AuditSettings Audit { get; set; } = new();
}

public class SafetySettings
{
    public bool RequireConfirmation { get; set; } = true;
    public bool EnableDryRun { get; set; } = true;
    public List<string> CriticalOperations { get; set; } = ["DROP", "TRUNCATE", "DELETE", "ALTER", "GRANT", "REVOKE"];
}

public class AuditSettings
{
    public bool Enabled { get; set; } = true;
    public string LogPath { get; set; } = "audit.log";
    public bool LogToConsole { get; set; } = false;
}
