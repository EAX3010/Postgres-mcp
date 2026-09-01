using PostgresMcpServer.Services;
using PostgresMcpServer.Tools;

namespace PostgresMcpServer.Tests;

public class SqlTextTests
{
    // ---------- skeletonising ----------

    [Fact]
    public void LineCommentContentsAreBlanked()
    {
        Assert.False(SqlText.ContainsKeyword("SELECT 1 -- DROP TABLE users", "DROP"));
    }

    [Fact]
    public void BlockCommentContentsAreBlanked()
    {
        Assert.False(SqlText.ContainsKeyword("SELECT /* DROP TABLE users */ 1", "DROP"));
    }

    [Fact]
    public void NestedBlockCommentsAreHandled()
    {
        Assert.False(SqlText.ContainsKeyword("SELECT /* a /* b */ DROP */ 1", "DROP"));
    }

    [Fact]
    public void StringLiteralContentsAreBlanked()
    {
        Assert.False(SqlText.ContainsKeyword("INSERT INTO t VALUES ('DROP TABLE x')", "DROP"));
    }

    [Fact]
    public void EscapedQuotesInsideLiteralsAreHandled()
    {
        Assert.False(SqlText.ContainsKeyword("SELECT 'it''s a DROP' AS a", "DROP"));
    }

    [Fact]
    public void DollarQuotedBodiesAreBlanked()
    {
        Assert.False(SqlText.ContainsKeyword("DO $$ BEGIN DROP TABLE x; END $$", "DROP"));
    }

    [Fact]
    public void PositionalParametersAreNotMistakenForDollarQuotes()
    {
        // $1 and $2 must not be read as opening a dollar-quoted string, which would swallow
        // the rest of the statement.
        Assert.True(SqlText.ContainsKeyword("DELETE FROM t WHERE a = $1 AND b = $2", "DELETE"));
        Assert.True(SqlText.ContainsKeyword("DELETE FROM t WHERE a = $1 AND b = $2", "WHERE"));
    }

    [Fact]
    public void KeywordMatchingRespectsWordBoundaries()
    {
        Assert.False(SqlText.ContainsKeyword("SELECT dropped_at FROM t", "DROP"));
        Assert.False(SqlText.ContainsKeyword("SELECT where_clause FROM t", "WHERE"));
        Assert.True(SqlText.ContainsKeyword("SELECT * FROM t WHERE a = 1", "WHERE"));
    }

    [Fact]
    public void QuotedIdentifiersDoNotMatchKeywords()
    {
        Assert.False(SqlText.ContainsKeyword("SELECT * FROM \"DROP\"", "DROP"));
    }

    // ---------- statement counting ----------

    [Theory]
    [InlineData("SELECT 1", 1)]
    [InlineData("SELECT 1;", 1)]
    [InlineData(";SELECT 1", 1)]
    [InlineData("SELECT 1; SELECT 2", 2)]
    [InlineData("SELECT 'a;b'", 1)]
    [InlineData("SELECT 1 -- ; not a statement", 1)]
    [InlineData("DO $$ BEGIN a; b; END $$", 1)]
    public void StatementCountIgnoresSeparatorsInsideLiteralsAndComments(string sql, int expected)
    {
        Assert.Equal(expected, SqlText.StatementCount(sql));
    }

    // ---------- identifier quoting ----------

    [Fact]
    public void IdentifiersAreQuotedAndEscaped()
    {
        Assert.Equal("\"users\"", SqlText.QuoteIdentifier("users"));
        Assert.Equal("\"we\"\"ird\"", SqlText.QuoteIdentifier("we\"ird"));
    }

    [Fact]
    public void QuotingNeutralisesAnInjectedStatement()
    {
        var quoted = SqlText.QuoteIdentifier("a; DROP DATABASE prod; --");

        Assert.Equal("\"a; DROP DATABASE prod; --\"", quoted);
        // The whole payload is now one identifier, not three statements.
        Assert.Equal(1, SqlText.StatementCount($"DROP TABLE {quoted}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIdentifiersAreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => SqlText.QuoteIdentifier(name));
    }

    [Fact]
    public void OverlongIdentifiersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SqlText.QuoteIdentifier(new string('a', 64)));
    }

    [Fact]
    public void QualifiedNamesQuoteBothParts()
    {
        Assert.Equal("\"public\".\"users\"", SqlText.QuoteQualified("public", "users"));
    }

    // ---------- literal quoting ----------

    [Fact]
    public void LiteralsEscapeEmbeddedQuotes()
    {
        Assert.Equal("'O''Brien'", SqlText.QuoteLiteral("O'Brien"));
    }

    [Fact]
    public void PasswordContainingAQuoteDoesNotBreakTheStatement()
    {
        var sql = $"CREATE ROLE {SqlText.QuoteIdentifier("app")} LOGIN PASSWORD {SqlText.QuoteLiteral("pa'ss")}";

        Assert.Equal(1, SqlText.StatementCount(sql));
    }

    // ---------- redaction ----------

    [Fact]
    public void PasswordLiteralsAreRedacted()
    {
        var redacted = SqlText.RedactSecrets("CREATE ROLE app LOGIN PASSWORD 'hunter2' CREATEDB");

        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RedactionHandlesEscapedQuotesInThePassword()
    {
        var redacted = SqlText.RedactSecrets("CREATE ROLE app LOGIN PASSWORD 'pa''ss'");

        Assert.DoesNotContain("pa''ss", redacted);
    }

    [Fact]
    public void RedactionLeavesOtherStatementsAlone()
    {
        const string sql = "SELECT * FROM users WHERE id = 1";

        Assert.Equal(sql, SqlText.RedactSecrets(sql));
    }

    // ---------- privilege validation ----------

    [Fact]
    public void PrivilegesAreNormalised()
    {
        Assert.Equal("SELECT, INSERT", AdminTools.NormalizePrivileges("select, insert"));
    }

    [Theory]
    [InlineData("SELECT; DROP TABLE users")]
    [InlineData("ALL PRIVILEGES ON x TO y; DELETE FROM z")]
    [InlineData("NONSENSE")]
    public void UnsupportedPrivilegesAreRejected(string privileges)
    {
        Assert.Throws<ArgumentException>(() => AdminTools.NormalizePrivileges(privileges));
    }

    [Fact]
    public void EmptyPrivilegeListIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AdminTools.NormalizePrivileges("  "));
    }
}
