using Microsoft.Extensions.Options;
using PostgresMcpServer.Models;

namespace PostgresMcpServer.Services;

public interface ISafetyGuard
{
    SafetyCheckResult CheckQuery(string query);
    string GetDryRunMessage(string query, string database);
    SafetySettings Settings { get; }
}

public class SafetyCheckResult
{
    public string OperationType { get; set; } = "UNKNOWN";
    public bool IsCritical { get; set; }
    public bool RequiresConfirmation { get; set; }
    public RiskLevel RiskLevel { get; set; } = RiskLevel.Low;
    public List<string> Warnings { get; set; } = [];

    /// <summary>True only for statements PostgreSQL itself will accept in a READ ONLY transaction.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Set when the statement must not run at all, whatever the caller passes.</summary>
    public string? RejectionReason { get; set; }
    public bool IsRejected => RejectionReason != null;
}

public class SafetyGuard : ISafetyGuard
{
    private static readonly string[] KnownOperations =
    [
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP",
        "TRUNCATE", "GRANT", "REVOKE", "EXPLAIN", "WITH", "COPY", "DO", "CALL",
        "VACUUM", "ANALYZE", "REINDEX", "REFRESH", "CLUSTER", "COMMENT", "LOCK",
        "SET", "RESET", "SHOW", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "PREPARE", "EXECUTE", "DEALLOCATE", "LISTEN", "NOTIFY", "TABLE", "VALUES"
    ];

    private static readonly string[] DataModifyingKeywords = ["INSERT", "UPDATE", "DELETE", "MERGE"];

    /// <summary>Statements a READ ONLY transaction will accept.</summary>
    private static readonly string[] ReadOnlyOperations = ["SELECT", "EXPLAIN", "SHOW", "TABLE", "VALUES"];

    private readonly SafetySettings _settings;

    public SafetyGuard(IOptions<DatabaseConfig> config) => _settings = config.Value.Safety;

    public SafetySettings Settings => _settings;

    public SafetyCheckResult CheckQuery(string query)
    {
        var result = new SafetyCheckResult();
        var sql = query ?? string.Empty;
        var skeleton = SqlText.Skeleton(sql);

        if (skeleton.Trim().Length == 0)
        {
            result.RejectionReason = "The statement is empty.";
            return result;
        }

        // Multi-statement input defeats every per-statement check below, so refuse it up front
        // rather than analysing only the first statement and then executing all of them.
        if (!_settings.AllowMultiStatement && SqlText.IsMultiStatement(sql))
        {
            result.OperationType = DetectOperationType(skeleton);
            result.RejectionReason =
                "Multiple SQL statements in a single call are not allowed. Send one statement, " +
                "or use execute_batch, which runs each statement separately inside one transaction.";
            result.RiskLevel = RiskLevel.Critical;
            result.IsCritical = true;
            result.RequiresConfirmation = _settings.RequireConfirmation;
            result.Warnings.Add($"Input contains {SqlText.StatementCount(sql)} statements.");
            return result;
        }

        result.OperationType = DetectOperationType(skeleton);
        result.IsReadOnly = DetermineReadOnly(result.OperationType, skeleton);

        foreach (var criticalOp in _settings.EffectiveCriticalOperations)
        {
            if (SqlText.HasWord(skeleton, criticalOp))
            {
                result.IsCritical = true;
                result.Warnings.Add($"Contains critical operation: {criticalOp}");
            }
        }

        result.RiskLevel = DetermineRiskLevel(result, skeleton);

        if (result.OperationType == "DROP" && SqlText.HasWord(skeleton, "DATABASE"))
            result.Warnings.Add("DROP DATABASE detected - this permanently deletes the entire database.");

        if (SqlText.HasWord(skeleton, "TRUNCATE"))
            result.Warnings.Add("TRUNCATE removes all rows from the table.");

        if (result.OperationType == "DELETE" && !SqlText.HasWord(skeleton, "WHERE"))
            result.Warnings.Add("DELETE without a WHERE clause removes every row.");

        if (result.OperationType == "UPDATE" && !SqlText.HasWord(skeleton, "WHERE"))
            result.Warnings.Add("UPDATE without a WHERE clause modifies every row.");

        if (result.OperationType == "UNKNOWN")
            result.Warnings.Add("Statement type could not be determined; treating it as a high-risk write.");

        // Confirmation is driven by assessed risk, not only by keyword membership, so a
        // dangerous statement cannot slip through by using a verb that is not on the list.
        result.RequiresConfirmation =
            _settings.RequireConfirmation &&
            (result.IsCritical || result.RiskLevel >= _settings.ConfirmAtRiskLevel);

        return result;
    }

    private static string DetectOperationType(string skeleton)
    {
        var text = skeleton.TrimStart(' ', '\t', '\r', '\n', ';', '(');
        var end = 0;
        while (end < text.Length && (char.IsLetter(text[end]) || text[end] == '_')) end++;
        if (end == 0) return "UNKNOWN";

        var first = text[..end];
        if (!KnownOperations.Contains(first)) return "UNKNOWN";

        // A data-modifying CTE - WITH x AS (DELETE ...) ... - is a write, however it starts.
        if (first == "WITH")
        {
            var modifying = DataModifyingKeywords.FirstOrDefault(k => SqlText.HasWord(skeleton, k));
            return modifying ?? "SELECT";
        }

        return first;
    }

    private static bool DetermineReadOnly(string operation, string skeleton)
    {
        if (!ReadOnlyOperations.Contains(operation)) return false;

        // EXPLAIN ANALYZE genuinely executes the statement it is given.
        if (operation == "EXPLAIN" && SqlText.HasWord(skeleton, "ANALYZE")) return false;

        // SELECT ... FOR UPDATE / FOR SHARE takes row locks; a read-only transaction rejects it.
        if (SqlText.HasWord(skeleton, "FOR") &&
            (SqlText.HasWord(skeleton, "UPDATE") || SqlText.HasWord(skeleton, "SHARE"))) return false;

        return !DataModifyingKeywords.Any(k => SqlText.HasWord(skeleton, k));
    }

    private static RiskLevel DetermineRiskLevel(SafetyCheckResult result, string skeleton)
    {
        var op = result.OperationType;

        if (op == "DROP" && (SqlText.HasWord(skeleton, "DATABASE") || SqlText.HasWord(skeleton, "SCHEMA")))
            return RiskLevel.Critical;

        if (op == "TRUNCATE") return RiskLevel.Critical;

        if (op == "DELETE" && !SqlText.HasWord(skeleton, "WHERE")) return RiskLevel.Critical;
        if (op == "UPDATE" && !SqlText.HasWord(skeleton, "WHERE")) return RiskLevel.High;

        if (op is "DROP" or "ALTER" or "GRANT" or "REVOKE") return RiskLevel.High;

        // Anything we could not classify is treated as dangerous rather than harmless.
        if (op is "UNKNOWN" or "DO" or "CALL" or "COPY") return RiskLevel.High;

        if (result.IsReadOnly) return RiskLevel.Low;

        if (op is "DELETE" or "MERGE") return RiskLevel.High;
        if (op is "INSERT" or "UPDATE" or "CREATE") return RiskLevel.Medium;

        return RiskLevel.Medium;
    }

    public string GetDryRunMessage(string query, string database)
    {
        var check = CheckQuery(query);
        var warnings = check.Warnings.Count > 0
            ? $"Warnings:\n  - {string.Join("\n  - ", check.Warnings)}"
            : string.Empty;
        var rejection = check.IsRejected ? $"REJECTED: {check.RejectionReason}\n" : string.Empty;
        var next = check.RequiresConfirmation ? " and confirm to true" : "";

        return $"""
            [DRY RUN] Would execute on database '{database}':
            Operation: {check.OperationType}
            Risk Level: {check.RiskLevel.ToWireString().ToUpperInvariant()}
            Read-only: {check.IsReadOnly}
            Query: {SqlText.RedactSecrets(query)}
            {rejection}{warnings}

            To execute this statement, set dryRun to false{next}.
            """;
    }
}
