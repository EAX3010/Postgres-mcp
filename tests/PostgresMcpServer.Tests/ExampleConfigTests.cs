using Microsoft.Extensions.Configuration;
using Npgsql;
using PostgresMcpServer.Models;
using System.Text.Json;

namespace PostgresMcpServer.Tests;

/// <summary>
/// The shipped example files are the first thing a new user copies, so they are validated
/// here: every setting must bind, every example connection string must parse, and every MCP
/// client config must be valid and point at the executable.
/// </summary>
public class ExampleConfigTests
{
    private static readonly string ExamplesDir = Path.Combine(AppContext.BaseDirectory, "examples");

    private static DatabaseConfig BindExample()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.example.json", optional: false)
            .Build();

        return configuration.Get<DatabaseConfig>()!;
    }

    // ---------- appsettings.example.json ----------

    [Fact]
    public void ExampleConfigBinds() => Assert.NotEmpty(BindExample().Databases);

    [Fact]
    public void EveryExampleConnectionStringParses()
    {
        foreach (var (name, connectionString) in BindExample().Databases)
        {
            var exception = Record.Exception(() => new NpgsqlConnectionStringBuilder(connectionString));
            Assert.True(exception is null, $"Database '{name}' has an unparseable connection string: {exception?.Message}");
        }
    }

    [Fact]
    public void EveryExampleConnectionStringNamesAHostAndDatabase()
    {
        foreach (var (name, connectionString) in BindExample().Databases)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);

            Assert.False(string.IsNullOrWhiteSpace(builder.Host), $"Database '{name}' has no Host.");
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), $"Database '{name}' has no Database.");
            Assert.False(string.IsNullOrWhiteSpace(builder.Username), $"Database '{name}' has no Username.");
        }
    }

    [Fact]
    public void ExampleDoesNotShipARealLookingPassword()
    {
        foreach (var (name, connectionString) in BindExample().Databases)
        {
            var password = new NpgsqlConnectionStringBuilder(connectionString).Password;

            Assert.True(
                password is null || password.Contains("YOUR_") || password.Contains("REPLACE"),
                $"Database '{name}' appears to ship a real password.");
        }
    }

    [Fact]
    public void ExampleSafetySettingsMatchTheDocumentedDefaults()
    {
        var safety = BindExample().Safety;

        Assert.True(safety.RequireConfirmation);
        Assert.True(safety.EnableDryRun);
        Assert.False(safety.AllowMultiStatement);
        Assert.Equal(RiskLevel.High, safety.ConfirmAtRiskLevel);
    }

    [Fact]
    public void ExampleCriticalOperationsDoNotDuplicateWhenBound()
    {
        // Binding a list onto a pre-populated property used to append rather than replace,
        // producing twelve entries from the six in this very file.
        Assert.Equal(6, BindExample().Safety.EffectiveCriticalOperations.Count);
    }

    [Fact]
    public void ExampleLimitsAreUsable()
    {
        var limits = BindExample().Limits;

        Assert.True(limits.MaxRows > 0);
        Assert.True(limits.MaxResponseBytes > 0);
        Assert.True(limits.CommandTimeoutSeconds >= 0);
    }

    [Fact]
    public void ExampleAuditSettingsBind()
    {
        var audit = BindExample().Audit;

        Assert.True(audit.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(audit.LogPath));
    }

    // ---------- examples/*.json, one per MCP client ----------

    /// <summary>Clients disagree on the top-level key: VS Code uses servers, Zed uses context_servers.</summary>
    private static readonly string[] ServerCollectionKeys = ["mcpServers", "servers", "context_servers"];

    public static TheoryData<string> ClientExamples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(ExamplesDir, "*.json"))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Fact]
    public void EveryDocumentedClientHasAnExample()
    {
        var present = Directory.GetFiles(ExamplesDir, "*.json").Select(Path.GetFileName).ToList();

        foreach (var expected in new[]
                 {
                     "claude-desktop.json", "claude-code.mcp.json", "cursor.json", "vs-code.json",
                     "windsurf.json", "cline.json", "gemini-cli.json", "zed.json", "env-only.json"
                 })
        {
            Assert.Contains(expected, present);
        }
    }

    [Theory]
    [MemberData(nameof(ClientExamples))]
    public void ClientExampleIsValidJsonAndPointsAtTheExecutable(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(ExamplesDir, fileName));
        using var document = JsonDocument.Parse(json);

        var key = ServerCollectionKeys.FirstOrDefault(k => document.RootElement.TryGetProperty(k, out _));
        Assert.True(key is not null,
            $"'{fileName}' has none of: {string.Join(", ", ServerCollectionKeys)}.");

        var servers = document.RootElement.GetProperty(key!);
        Assert.NotEmpty(servers.EnumerateObject());

        foreach (var server in servers.EnumerateObject())
        {
            Assert.True(server.Value.TryGetProperty("command", out var commandElement),
                $"'{fileName}' server '{server.Name}' has no command.");

            var command = commandElement.GetString();
            Assert.False(string.IsNullOrWhiteSpace(command), $"'{fileName}' server '{server.Name}' has an empty command.");
            Assert.Contains("PostgresMcpServer", command);

            // A relative path is resolved inconsistently between clients.
            Assert.True(Path.IsPathRooted(command), $"'{fileName}' server '{server.Name}' should use an absolute path.");
        }
    }

    [Fact]
    public void VsCodeExampleUsesTheKeysVsCodeExpects()
    {
        var json = File.ReadAllText(Path.Combine(ExamplesDir, "vs-code.json"));
        using var document = JsonDocument.Parse(json);

        // VS Code rejects the "mcpServers" shape every other client uses.
        Assert.False(document.RootElement.TryGetProperty("mcpServers", out _));
        Assert.Equal("stdio",
            document.RootElement.GetProperty("servers").GetProperty("postgres").GetProperty("type").GetString());
    }

    [Fact]
    public void ZedExampleUsesContextServers()
    {
        var json = File.ReadAllText(Path.Combine(ExamplesDir, "zed.json"));
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("context_servers", out _));
    }

    [Fact]
    public void EnvironmentOverrideExampleUsesThePrefixTheServerReads()
    {
        var json = File.ReadAllText(Path.Combine(ExamplesDir, "env-only.json"));
        using var document = JsonDocument.Parse(json);

        var env = document.RootElement
            .GetProperty("mcpServers")
            .GetProperty("postgres")
            .GetProperty("env");

        foreach (var variable in env.EnumerateObject())
            Assert.StartsWith("POSTGRESMCP_", variable.Name);

        // Double underscore is the section separator the configuration binder expects.
        Assert.Contains(env.EnumerateObject(), v => v.Name.Contains("Databases__"));
    }

    [Fact]
    public void EnvironmentOverrideExampleConnectionStringParses()
    {
        var json = File.ReadAllText(Path.Combine(ExamplesDir, "env-only.json"));
        using var document = JsonDocument.Parse(json);

        var connectionString = document.RootElement
            .GetProperty("mcpServers").GetProperty("postgres").GetProperty("env")
            .GetProperty("POSTGRESMCP_Databases__local").GetString();

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.Equal("localhost", builder.Host);
    }

    [Fact]
    public void EnvironmentOverridesActuallyReachTheConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["POSTGRESMCP_Databases__from_env"] = "Host=localhost;Database=d;Username=u",
                ["POSTGRESMCP_Limits__MaxRows"] = "250"
            }.ToDictionary(kv => kv.Key.Replace("POSTGRESMCP_", "").Replace("__", ":"), kv => kv.Value))
            .Build();

        var config = configuration.Get<DatabaseConfig>()!;

        Assert.Contains("from_env", config.Databases.Keys);
        Assert.Equal(250, config.Limits.MaxRows);
    }
}
