using Microsoft.Extensions.Options;
using PostgresMcpServer.Models;
using System.Text.RegularExpressions;

namespace PostgresMcpServer.Services;

public interface ISafetyGuard
{
    SafetyCheckResult CheckQuery(string query);
    string GetDryRunMessage(string query, string database);
}

public class SafetyCheckResult
{
    public bool IsCritical { get; set; }
    public bool RequiresConfirmation { get; set; }
    public string RiskLevel { get; set; } = "low"; // low, medium, high, critical
    public List<string> Warnings { get; set; } = [];
    public string OperationType { get; set; } = string.Empty;
}

public partial class SafetyGuard : ISafetyGuard
{
    private readonly SafetySettings _settings;

    public SafetyGuard(IOptions<DatabaseConfig> config)
    {
        _settings = config.Value.Safety;
    }

    public SafetyCheckResult CheckQuery(string query)
    {
        var result = new SafetyCheckResult();
        var upperQuery = query.ToUpperInvariant().Trim();

        // Detect operation type
        result.OperationType = DetectOperationType(upperQuery);

        // Check for critical operations
        foreach (var criticalOp in _settings.CriticalOperations)
        {
            if (upperQuery.StartsWith(criticalOp) || upperQuery.Contains($" {criticalOp} "))
            {
                result.IsCritical = true;
                result.RequiresConfirmation = _settings.RequireConfirmation;
                result.Warnings.Add($"Contains critical operation: {criticalOp}");
            }
        }

        // Set risk level
        result.RiskLevel = DetermineRiskLevel(upperQuery, result);

        // Additional safety checks
        if (upperQuery.Contains("DROP DATABASE"))
        {
            result.RiskLevel = "critical";
            result.Warnings.Add("DROP DATABASE detected - this will permanently delete the entire database");
        }

        if (upperQuery.Contains("TRUNCATE"))
        {
            result.Warnings.Add("TRUNCATE will remove all rows from the table");
        }

        if (DeleteWithoutWhereRegex().IsMatch(upperQuery))
        {
            result.Warnings.Add("DELETE without WHERE clause will remove all rows");
            result.RiskLevel = "critical";
        }

        if (UpdateWithoutWhereRegex().IsMatch(upperQuery))
        {
            result.Warnings.Add("UPDATE without WHERE clause will modify all rows");
            result.RiskLevel = "high";
        }

        return result;
    }

    private static string DetectOperationType(string query)
    {
        return query switch
        {
            _ when query.StartsWith("SELECT") => "SELECT",
            _ when query.StartsWith("INSERT") => "INSERT",
            _ when query.StartsWith("UPDATE") => "UPDATE",
            _ when query.StartsWith("DELETE") => "DELETE",
            _ when query.StartsWith("CREATE") => "CREATE",
            _ when query.StartsWith("ALTER") => "ALTER",
            _ when query.StartsWith("DROP") => "DROP",
            _ when query.StartsWith("TRUNCATE") => "TRUNCATE",
            _ when query.StartsWith("GRANT") => "GRANT",
            _ when query.StartsWith("REVOKE") => "REVOKE",
            _ when query.StartsWith("EXPLAIN") => "EXPLAIN",
            _ => "UNKNOWN"
        };
    }

    private static string DetermineRiskLevel(string query, SafetyCheckResult result)
    {
        if (query.StartsWith("SELECT") || query.StartsWith("EXPLAIN"))
            return "low";

        if (query.StartsWith("INSERT"))
            return "medium";

        if (result.IsCritical)
            return "high";

        return "medium";
    }

    public string GetDryRunMessage(string query, string database)
    {
        var check = CheckQuery(query);
        return $"""
            [DRY RUN] Would execute on database '{database}':
            Operation: {check.OperationType}
            Risk Level: {check.RiskLevel.ToUpperInvariant()}
            Query: {query}
            {(check.Warnings.Count > 0 ? $"Warnings:\n  - {string.Join("\n  - ", check.Warnings)}" : "")}

            To execute this query, set dryRun to false.
            """;
    }

    [GeneratedRegex(@"DELETE\s+FROM\s+\w+\s*(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteWithoutWhereRegex();

    [GeneratedRegex(@"UPDATE\s+\w+\s+SET\s+[^;]+(?!WHERE)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateWithoutWhereRegex();
}
