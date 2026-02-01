using System.Text.Json;

namespace PostgresMcpServer.Models;

public class AuditEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? InstanceId { get; set; }
    public string Database { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string User { get; set; } = Environment.UserName;
    public bool DryRun { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int RowsAffected { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
}
