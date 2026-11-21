using Npgsql;
using PostgresMcpServer.Services;

namespace PostgresMcpServer.Tests;

/// <summary>
/// Hosting dashboards and .env files hand out postgres:// URLs, which Npgsql does not read.
/// These pin down the translation, and that a URL which fails to parse never has part of
/// itself - usually part of a password - quoted back in the error.
/// </summary>
public class ConnectionStringsTests
{
    [Fact]
    public void KeywordStringIsPassedThroughUntouched()
    {
        const string keywords = "Host=localhost;Port=5432;Database=myapp;Username=me;Password=secret";

        Assert.Equal(keywords, ConnectionStrings.Normalize(keywords));
    }

    [Theory]
    [InlineData("postgres://me:secret@db.example.com:6543/myapp")]
    [InlineData("postgresql://me:secret@db.example.com:6543/myapp")]
    [InlineData("  POSTGRES://me:secret@db.example.com:6543/myapp  ")]
    public void UrlIsTranslated(string url)
    {
        var parsed = ConnectionStrings.Parse(url);

        Assert.Equal("db.example.com", parsed.Host);
        Assert.Equal(6543, parsed.Port);
        Assert.Equal("myapp", parsed.Database);
        Assert.Equal("me", parsed.Username);
        Assert.Equal("secret", parsed.Password);
    }

    [Fact]
    public void NormalizedUrlIsAcceptedByNpgsql()
    {
        var normalized = ConnectionStrings.Normalize("postgres://me:secret@db.example.com/myapp?sslmode=require");

        // This is the call that used to throw "Format of the initialization string...".
        using var dataSource = NpgsqlDataSource.Create(normalized);
        Assert.Contains("db.example.com", dataSource.ConnectionString);
    }

    [Fact]
    public void PortDefaultsWhenTheUrlOmitsIt()
    {
        Assert.Equal(5432, ConnectionStrings.Parse("postgres://me:secret@db.example.com/myapp").Port);
    }

    [Fact]
    public void HostDefaultsToLocalhost()
    {
        Assert.Equal("localhost", ConnectionStrings.Parse("postgres:///myapp").Host);
    }

    [Fact]
    public void CredentialsAreOptional()
    {
        var parsed = ConnectionStrings.Parse("postgres://localhost/myapp");

        Assert.Equal("localhost", parsed.Host);
        Assert.Equal("myapp", parsed.Database);
        Assert.Null(parsed.Password);
    }

    [Fact]
    public void PercentEncodedPasswordIsDecoded()
    {
        Assert.Equal("p@ss/w:rd?", ConnectionStrings.Parse("postgres://me:p%40ss%2Fw%3Ard%3F@host/db").Password);
    }

    [Theory]
    [InlineData("postgres://me:p@ss@host/db", "p@ss")]
    [InlineData("postgres://me:pa:ss@host/db", "pa:ss")]
    [InlineData("postgres://me:pa/ss@host/db", "pa/ss")]
    [InlineData("postgres://me:pa#ss@host/db", "pa#ss")]
    public void CommonUnencodedPasswordCharactersStillParse(string url, string password)
    {
        var parsed = ConnectionStrings.Parse(url);

        Assert.Equal(password, parsed.Password);
        Assert.Equal("host", parsed.Host);
        Assert.Equal("db", parsed.Database);
    }

    [Fact]
    public void PasswordThatNeedsQuotingSurvivesTheRoundTrip()
    {
        // A semicolon or quote would end the value early if it were interpolated naively.
        var normalized = ConnectionStrings.Normalize("postgres://me:a%3Bb'c%20d%3D@host/db");

        Assert.Equal("a;b'c d=", new NpgsqlConnectionStringBuilder(normalized).Password);
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    public void LibpqSslModesAreUnderstood(string sslmode, SslMode expected)
    {
        Assert.Equal(expected, ConnectionStrings.Parse($"postgres://me:pw@host/db?sslmode={sslmode}").SslMode);
    }

    [Fact]
    public void LibpqParametersMapToNpgsqlKeywords()
    {
        var parsed = ConnectionStrings.Parse(
            "postgres://me:pw@host/db?sslrootcert=%2Fcerts%2Fca.pem&application_name=mcp&connect_timeout=7&channel_binding=require");

        Assert.Equal("/certs/ca.pem", parsed.RootCertificate);
        Assert.Equal("mcp", parsed.ApplicationName);
        Assert.Equal(7, parsed.Timeout);
        Assert.Equal(ChannelBinding.Require, parsed.ChannelBinding);
    }

    // Every parameter configuration.md promises. A name Npgsql does not know would throw.
    [Theory]
    [InlineData("sslmode=prefer")]
    [InlineData("sslrootcert=ca.pem")]
    [InlineData("sslcert=client.pem")]
    [InlineData("sslkey=client.key")]
    [InlineData("sslpassword=keypass")]
    [InlineData("channel_binding=prefer")]
    [InlineData("application_name=mcp")]
    [InlineData("connect_timeout=10")]
    [InlineData("options=-c%20statement_timeout%3D5000")]
    public void EveryDocumentedUrlParameterIsAccepted(string parameter)
    {
        var normalized = ConnectionStrings.Normalize($"postgres://me:pw@host/db?{parameter}");

        using var dataSource = NpgsqlDataSource.Create(normalized);
        Assert.Equal("host", new NpgsqlConnectionStringBuilder(normalized).Host);
    }

    [Fact]
    public void NpgsqlKeywordsWorkAsUrlParameters()
    {
        var parsed = ConnectionStrings.Parse("postgres://me:pw@host/db?Include%20Error%20Detail=true&Search%20Path=analytics");

        Assert.True(parsed.IncludeErrorDetail);
        Assert.Equal("analytics", parsed.SearchPath);
    }

    [Fact]
    public void Ipv6HostIsUnwrapped()
    {
        var parsed = ConnectionStrings.Parse("postgres://me:pw@[::1]:5433/db");

        Assert.Equal("::1", parsed.Host);
        Assert.Equal(5433, parsed.Port);
    }

    // The shapes the common hosts hand out, with credentials replaced.
    [Theory]
    [InlineData("postgresql://neondb_owner:npg_abc123@ep-cool-name-a1b2c3.eu-central-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require",
        "ep-cool-name-a1b2c3.eu-central-1.aws.neon.tech", "neondb", "neondb_owner")]
    [InlineData("postgresql://postgres.abcdefghijklmnop:pw@aws-0-eu-central-1.pooler.supabase.com:6543/postgres",
        "aws-0-eu-central-1.pooler.supabase.com", "postgres", "postgres.abcdefghijklmnop")]
    [InlineData("postgres://app_user:pw@dpg-abc123-a.frankfurt-postgres.render.com/app_db",
        "dpg-abc123-a.frankfurt-postgres.render.com", "app_db", "app_user")]
    [InlineData("postgresql://postgres:pw@containers-us-west-1.railway.app:7432/railway",
        "containers-us-west-1.railway.app", "railway", "postgres")]
    public void UrlsInTheShapeHostsPublishParse(string url, string host, string database, string username)
    {
        var parsed = ConnectionStrings.Parse(url);

        Assert.Equal(host, parsed.Host);
        Assert.Equal(database, parsed.Database);
        Assert.Equal(username, parsed.Username);
    }

    [Theory]
    [InlineData("postgres://me:hunter2@host/db?pgbouncer=true")]            // unknown parameter
    [InlineData("postgres://me:hunter2@host/db?sslmode=sometimes")]         // bad value
    [InlineData("postgres://me:hunter2@host:notaport/db")]                  // bad port
    [InlineData("postgres://me:hunter2?x@host/db")]                         // unencoded ? in password
    [InlineData("postgres://me:hunter2?x=1@host/db")]
    [InlineData("postgres://me:5432?hunter2@host/db")]
    public void ErrorsNeverQuoteThePassword(string url)
    {
        var exception = Assert.Throws<FormatException>(() => ConnectionStrings.Parse(url));

        Assert.DoesNotContain("hunter2", exception.Message);
    }

    [Fact]
    public void UnknownParameterIsNamedSoItCanBeRemoved()
    {
        var exception = Assert.Throws<FormatException>(
            () => ConnectionStrings.Parse("postgres://me:pw@host/db?pgbouncer=true"));

        Assert.Contains("pgbouncer", exception.Message);
    }

    [Fact]
    public void AValueThatIsNeitherFormStillFails()
    {
        Assert.ThrowsAny<Exception>(() => ConnectionStrings.Parse("not a connection string"));
    }
}
