using Microsoft.Extensions.Options;
using PostgresMcpServer.Models;
using PostgresMcpServer.Services;

namespace PostgresMcpServer.Tests;

/// <summary>
/// Regression coverage for the confirmation bypasses that previously let destructive
/// statements through unconfirmed, and for the WHERE-clause detection that was inverted.
/// </summary>
public class SafetyGuardTests
{
    private static SafetyGuard Guard(Action<SafetySettings>? configure = null)
    {
        var config = new DatabaseConfig();
        configure?.Invoke(config.Safety);
        return new SafetyGuard(Options.Create(config));
    }

    // ---------- multi-statement input ----------

    [Theory]
    [InlineData("SELECT 1; DROP TABLE users")]
    [InlineData("SELECT 1; DROP TABLE users; --")]
    [InlineData("INSERT INTO t VALUES (1); DELETE FROM t")]
    public void MultiStatementInputIsRejected(string sql)
    {
        var result = Guard().CheckQuery(sql);

        Assert.True(result.IsRejected);
        Assert.Contains("Multiple SQL statements", result.RejectionReason);
    }

    [Fact]
    public void TrailingSemicolonIsNotMultiStatement()
    {
        Assert.False(Guard().CheckQuery("SELECT 1;").IsRejected);
    }

    [Fact]
    public void SemicolonInsideLiteralIsNotAStatementSeparator()
    {
        var result = Guard().CheckQuery("SELECT * FROM t WHERE note = 'a; b'");

        Assert.False(result.IsRejected);
        Assert.True(result.IsReadOnly);
    }

    // ---------- statements that used to evade confirmation ----------

    [Theory]
    [InlineData(";DROP TABLE users", "DROP")]
    [InlineData("/*c*/DROP TABLE users", "DROP")]
    [InlineData("  \n DROP TABLE users", "DROP")]
    public void LeadingNoiseStillResolvesTheOperation(string sql, string expected)
    {
        var result = Guard().CheckQuery(sql);

        Assert.Equal(expected, result.OperationType);
        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public void DataModifyingCteIsTreatedAsAWrite()
    {
        var result = Guard().CheckQuery("WITH t AS (DELETE FROM users RETURNING *) SELECT * FROM t");

        Assert.Equal("DELETE", result.OperationType);
        Assert.False(result.IsReadOnly);
        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public void UpdateCteIsTreatedAsAWrite()
    {
        var result = Guard().CheckQuery("WITH t AS (UPDATE users SET a = 1 RETURNING *) SELECT * FROM t");

        Assert.Equal("UPDATE", result.OperationType);
        Assert.False(result.IsReadOnly);
    }

    [Fact]
    public void PlainCteSelectRemainsReadOnly()
    {
        var result = Guard().CheckQuery("WITH t AS (SELECT 1 AS a) SELECT * FROM t");

        Assert.Equal("SELECT", result.OperationType);
        Assert.True(result.IsReadOnly);
        Assert.Equal(RiskLevel.Low, result.RiskLevel);
    }

    [Fact]
    public void DoBlockIsHighRiskAndRequiresConfirmation()
    {
        var result = Guard().CheckQuery("DO $$ BEGIN EXECUTE 'DROP TABLE users'; END $$");

        Assert.Equal("DO", result.OperationType);
        Assert.False(result.IsReadOnly);
        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public void UnknownStatementFailsClosed()
    {
        var result = Guard().CheckQuery("FROBNICATE THE WIDGETS");

        Assert.Equal("UNKNOWN", result.OperationType);
        Assert.Equal(RiskLevel.High, result.RiskLevel);
        Assert.True(result.RequiresConfirmation);
    }

    // ---------- WHERE-clause detection ----------

    [Fact]
    public void UpdateWithWhereIsNotFlaggedAsUnqualified()
    {
        var result = Guard().CheckQuery("UPDATE users SET name = 'X' WHERE id = 1");

        Assert.DoesNotContain(result.Warnings, w => w.Contains("without a WHERE"));
        Assert.NotEqual(RiskLevel.Critical, result.RiskLevel);
    }

    [Fact]
    public void UpdateWithoutWhereIsFlagged()
    {
        var result = Guard().CheckQuery("UPDATE users SET name = 'X'");

        Assert.Contains(result.Warnings, w => w.Contains("without a WHERE"));
        Assert.Equal(RiskLevel.High, result.RiskLevel);
        Assert.True(result.RequiresConfirmation);
    }

    [Theory]
    [InlineData("DELETE FROM users")]
    [InlineData("DELETE FROM public.users")]
    [InlineData("DELETE FROM \"users\"")]
    [InlineData("DELETE FROM \"public\".\"users\"")]
    public void DeleteWithoutWhereIsCriticalRegardlessOfHowTheTableIsWritten(string sql)
    {
        var result = Guard().CheckQuery(sql);

        Assert.Equal(RiskLevel.Critical, result.RiskLevel);
        Assert.Contains(result.Warnings, w => w.Contains("without a WHERE"));
    }

    [Fact]
    public void DeleteWithWhereIsNotCritical()
    {
        var result = Guard().CheckQuery("DELETE FROM public.users WHERE id = 1");

        Assert.NotEqual(RiskLevel.Critical, result.RiskLevel);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("without a WHERE"));
    }

    [Fact]
    public void WhereInsideAStringLiteralDoesNotCount()
    {
        var result = Guard().CheckQuery("DELETE FROM audit_log WHERE_NOT_REAL IS NULL");

        // The token above is WHERE_NOT_REAL, not WHERE, so this is still an unqualified delete.
        Assert.Equal(RiskLevel.Critical, result.RiskLevel);
    }

    // ---------- keyword matching must ignore literals and comments ----------

    [Fact]
    public void KeywordInsideAStringLiteralIsNotACriticalOperation()
    {
        var result = Guard().CheckQuery("INSERT INTO log (msg) VALUES ('user clicked DROP')");

        Assert.Equal("INSERT", result.OperationType);
        Assert.False(result.IsCritical);
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void KeywordInsideACommentIsNotACriticalOperation()
    {
        var result = Guard().CheckQuery("SELECT 1 -- DROP TABLE users");

        Assert.Equal("SELECT", result.OperationType);
        Assert.False(result.IsCritical);
        Assert.True(result.IsReadOnly);
    }

    // ---------- read-only classification ----------

    [Fact]
    public void SelectContainingTheWordLimitInALiteralIsStillReadOnly()
    {
        var result = Guard().CheckQuery("SELECT * FROM t WHERE note = 'no limit here'");

        Assert.True(result.IsReadOnly);
        Assert.Equal(RiskLevel.Low, result.RiskLevel);
    }

    [Fact]
    public void ExplainWithoutAnalyzeIsReadOnly()
    {
        Assert.True(Guard().CheckQuery("EXPLAIN SELECT * FROM users").IsReadOnly);
    }

    [Fact]
    public void ExplainAnalyzeIsNotReadOnly()
    {
        var result = Guard().CheckQuery("EXPLAIN ANALYZE DELETE FROM users");

        Assert.False(result.IsReadOnly);
    }

    [Fact]
    public void SelectForUpdateIsNotReadOnly()
    {
        Assert.False(Guard().CheckQuery("SELECT * FROM users FOR UPDATE").IsReadOnly);
    }

    // ---------- risk levels ----------

    [Theory]
    [InlineData("TRUNCATE TABLE users", RiskLevel.Critical)]
    [InlineData("DROP DATABASE production", RiskLevel.Critical)]
    [InlineData("DROP SCHEMA public CASCADE", RiskLevel.Critical)]
    [InlineData("DROP TABLE users", RiskLevel.High)]
    [InlineData("ALTER TABLE users ADD COLUMN a int", RiskLevel.High)]
    [InlineData("INSERT INTO users (a) VALUES (1)", RiskLevel.Medium)]
    [InlineData("SELECT 1", RiskLevel.Low)]
    public void RiskLevelsAreAssignedAsDocumented(string sql, RiskLevel expected)
    {
        Assert.Equal(expected, Guard().CheckQuery(sql).RiskLevel);
    }

    [Fact]
    public void ConfirmationCanBeDisabledEntirely()
    {
        var result = Guard(s => s.RequireConfirmation = false).CheckQuery("DROP TABLE users");

        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void ConfirmationThresholdIsHonoured()
    {
        var result = Guard(s =>
        {
            s.ConfirmAtRiskLevel = RiskLevel.Medium;
            s.CriticalOperations = ["NOTHING"];
        }).CheckQuery("INSERT INTO users (a) VALUES (1)");

        Assert.Equal(RiskLevel.Medium, result.RiskLevel);
        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public void EmptyStatementIsRejected()
    {
        Assert.True(Guard().CheckQuery("   ").IsRejected);
        Assert.True(Guard().CheckQuery("-- just a comment").IsRejected);
    }

    // ---------- configuration ----------

    [Fact]
    public void CriticalOperationsFallBackToDefaultsWithoutDuplicating()
    {
        var settings = new SafetySettings();

        Assert.Equal(SafetySettings.DefaultCriticalOperations.Length, settings.EffectiveCriticalOperations.Count);
    }

    [Fact]
    public void ConfiguredCriticalOperationsReplaceDefaultsRatherThanAppending()
    {
        // The configuration binder adds to the bound collection. When the property started
        // out pre-populated, binding the six documented defaults produced twelve entries and
        // every warning was emitted twice.
        var settings = new SafetySettings
        {
            CriticalOperations = ["DROP", "TRUNCATE", "DELETE", "ALTER", "GRANT", "REVOKE"]
        };

        Assert.Equal(6, settings.EffectiveCriticalOperations.Count);
    }

    [Fact]
    public void CriticalOperationWarningIsNotDuplicated()
    {
        var config = new DatabaseConfig();
        config.Safety.CriticalOperations = ["DROP", "DROP", "drop"];
        var result = new SafetyGuard(Options.Create(config)).CheckQuery("DROP TABLE users");

        Assert.Single(result.Warnings, w => w == "Contains critical operation: DROP");
    }
}
