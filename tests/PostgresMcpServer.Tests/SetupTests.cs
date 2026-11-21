using Npgsql;
using PostgresMcpServer.Services;
using PostgresMcpServer.Setup;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgresMcpServer.Tests;

/// <summary>A throwaway home directory, so nothing here touches the real client configs.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pgmcp-setup-" + Guid.NewGuid().ToString("N")[..8]);
    public string App => Path.Combine(Root, "app");
    public string Home => Path.Combine(Root, "home");
    public string AppData => Path.Combine(Root, "appdata");
    public SetupPaths Paths => new(HostPlatform.Windows, Home, AppData);
    public string Settings => Path.Combine(App, "appsettings.json");
    public string ClaudeDesktopConfig => Path.Combine(AppData, "Claude", "claude_desktop_config.json");

    public Sandbox()
    {
        Directory.CreateDirectory(App);
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(AppData);
    }

    public void Install(string clientName) =>
        Directory.CreateDirectory(McpClient.All.Single(c => c.Name == clientName).InstallDirectory(Paths)!);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* temp cleanup only */ }
    }
}

public class ConfigurationLoaderTests
{
    private const string Url = "postgres://me:pw@db.example.com/myapp";

    [Fact]
    public void DatabaseUrlIsUsedWhenNothingElseIsConfigured()
    {
        using var sandbox = new Sandbox();

        var (configuration, status) = ConfigurationLoader.Load(sandbox.App, Url);

        Assert.Equal(DatabaseUrlStatus.Used, status);
        Assert.Equal(Url, configuration["Databases:default"]);
    }

    [Fact]
    public void DatabaseUrlNeverAddsToAnExplicitConfiguration()
    {
        // A shell that exports the application's read-write URL must not widen a setup that
        // was deliberately pointed at a read-only role.
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.Settings, """{ "Databases": { "readonly": "Host=localhost;Database=d;Username=ro" } }""");

        var (configuration, status) = ConfigurationLoader.Load(sandbox.App, Url);

        Assert.Equal(DatabaseUrlStatus.Ignored, status);
        Assert.Equal(["readonly"], configuration.GetSection("Databases").GetChildren().Select(c => c.Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentDatabaseUrlChangesNothing(string? value)
    {
        using var sandbox = new Sandbox();

        var (configuration, status) = ConfigurationLoader.Load(sandbox.App, value);

        Assert.Equal(DatabaseUrlStatus.NotSet, status);
        Assert.Empty(configuration.GetSection("Databases").GetChildren());
    }
}

public class ClientConfigFileTests
{
    private const string Exe = @"C:\Tools\PostgresMcpServer\PostgresMcpServer.exe";

    private static JsonObject Entry(string command = Exe) => new() { ["command"] = command };

    private static string CommandIn(string path, string key = "mcpServers")
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(key).GetProperty("postgres").GetProperty("command").GetString()!;
    }

    [Fact]
    public void CreatesTheFileWhenTheClientHasNone()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Home, ".cursor", "mcp.json");

        var result = ClientConfigFile.Register(path, "mcpServers", Entry());

        Assert.Equal(RegistrationOutcome.Created, result.Outcome);
        // Read back through a JSON parser: the backslashes must have been escaped on the way in.
        Assert.Equal(Exe, CommandIn(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void AddsToAnExistingFileWithoutDisturbingIt()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Home, "config.json");
        const string original = """
            {
              "theme": "dark",
              "mcpServers": { "github": { "command": "npx", "args": ["-y", "github-mcp"] } }
            }
            """;
        File.WriteAllText(path, original);

        var result = ClientConfigFile.Register(path, "mcpServers", Entry());

        Assert.Equal(RegistrationOutcome.Added, result.Outcome);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("dark", document.RootElement.GetProperty("theme").GetString());
        Assert.Equal("npx", document.RootElement.GetProperty("mcpServers").GetProperty("github").GetProperty("command").GetString());
        Assert.Equal(Exe, CommandIn(path));

        Assert.Equal(original, File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void RegisteringTwiceWritesNothingTheSecondTime()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Home, "config.json");
        ClientConfigFile.Register(path, "mcpServers", Entry());
        var written = File.GetLastWriteTimeUtc(path);

        var result = ClientConfigFile.Register(path, "mcpServers", Entry());

        Assert.Equal(RegistrationOutcome.Unchanged, result.Outcome);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void AnEarlierInstallIsRepointedAndKeepsWhatTheUserAdded()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Home, "config.json");
        File.WriteAllText(path, """
            { "mcpServers": { "postgres": {
                "command": "/old/place/PostgresMcpServer",
                "env": { "POSTGRESMCP_Limits__MaxRows": "50" } } } }
            """);

        var result = ClientConfigFile.Register(path, "mcpServers", Entry());

        Assert.Equal(RegistrationOutcome.Updated, result.Outcome);
        Assert.Equal(Exe, CommandIn(path));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("50", document.RootElement.GetProperty("mcpServers").GetProperty("postgres")
            .GetProperty("env").GetProperty("POSTGRESMCP_Limits__MaxRows").GetString());
    }

    [Fact]
    public void ADifferentServerWithTheSameNameIsNotOverwrittenUnasked()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Home, "config.json");
        const string original = """{ "mcpServers": { "postgres": { "command": "npx", "args": ["-y", "some-other-postgres-server"] } } }""";
        File.WriteAllText(path, original);

        var refused = ClientConfigFile.Register(path, "mcpServers", Entry());

        Assert.Equal(RegistrationOutcome.Conflict, refused.Outcome);
        Assert.Equal("npx", refused.Detail);
        Assert.Equal(original, File.ReadAllText(path));

        var replaced = ClientConfigFile.Register(path, "mcpServers", Entry(), replaceOtherServer: true);

        Assert.Equal(RegistrationOutcome.Updated, replaced.Outcome);
        Assert.Equal(Exe, CommandIn(path));
        // The other server's arguments must not be left attached to this one.
        Assert.DoesNotContain("some-other-postgres-server", File.ReadAllText(path));
        Assert.Equal(original, File.ReadAllText(path + ".bak"));
    }

    [Theory]
    [InlineData("{\n  // my servers\n  \"mcpServers\": {}\n}", "comments")]
    [InlineData("{ \"mcpServers\": {}, }", "comments")]
    [InlineData("{ this is not json", "not valid JSON")]
    [InlineData("[1, 2, 3]", "JSON object")]
    [InlineData("{ \"mcpServers\": \"oops\" }", "not an object")]
    public void AFileThatCannotBeRewrittenFaithfullyIsLeftAlone(string original, string reason)
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Home, "settings.json");
        File.WriteAllText(path, original);

        var result = ClientConfigFile.Register(path, "mcpServers", Entry());

        Assert.Equal(RegistrationOutcome.NeedsManualEdit, result.Outcome);
        Assert.Contains(reason, result.Detail);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void AnEmptyFileIsTreatedAsNew()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Home, "settings.json");
        File.WriteAllText(path, "  \n");

        Assert.Equal(RegistrationOutcome.Added, ClientConfigFile.Register(path, "mcpServers", Entry()).Outcome);
        Assert.Equal(Exe, CommandIn(path));
    }

    // The registry the wizard writes from and the example files people copy by hand describe
    // the same thing twice. This keeps them from drifting apart.
    [Theory]
    [InlineData("Claude Desktop", "claude-desktop.json")]
    [InlineData("Cursor", "cursor.json")]
    [InlineData("VS Code", "vs-code.json")]
    [InlineData("Windsurf", "windsurf.json")]
    [InlineData("Cline", "cline.json")]
    [InlineData("Gemini CLI", "gemini-cli.json")]
    [InlineData("Zed", "zed.json")]
    public void WizardWritesTheSameShapeAsTheShippedExample(string clientName, string exampleFile)
    {
        var client = McpClient.All.Single(c => c.Name == clientName);
        var example = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", exampleFile)))!;

        var expected = example[client.ServersKey]!["postgres"]!;
        var command = expected["command"]!.GetValue<string>();

        Assert.True(JsonNode.DeepEquals(expected, client.NewEntry(command)),
            $"{exampleFile} and the {clientName} registration have drifted apart.");
    }

    [Fact]
    public void EveryFileBasedClientKnowsWhereItsConfigLives()
    {
        using var sandbox = new Sandbox();

        foreach (var platform in Enum.GetValues<HostPlatform>())
        {
            var paths = sandbox.Paths with { Platform = platform };

            foreach (var client in McpClient.All.Where(c => c.Command is null && c.InstallDirectory(paths) is not null))
                Assert.StartsWith(sandbox.Root, client.ConfigPath(paths));
        }
    }
}

public class SetupWizardTests
{
    private const string Url = "postgres://me:secret@db.example.com:6543/myapp";

    private static readonly Func<string, Task<ProbeResult>> Reachable =
        _ => Task.FromResult(new ProbeResult(true, "target", "Connected as me to myapp - PostgreSQL 18.4"));

    private static readonly Func<string, Task<ProbeResult>> Unreachable =
        _ => Task.FromResult(new ProbeResult(false, "target", "password authentication failed"));

    private static async Task<(int ExitCode, string Output)> Run(
        Sandbox sandbox,
        string[] answers,
        Func<string, Task<ProbeResult>>? probe = null,
        Dictionary<string, string>? existing = null)
    {
        var output = new StringWriter();
        var wizard = new SetupWizard(
            new StringReader(string.Join('\n', answers) + (answers.Length > 0 ? "\n" : "")),
            output,
            new SetupOptions
            {
                BaseDirectory = sandbox.App,
                ExecutablePath = Path.Combine(sandbox.App, "PostgresMcpServer.exe"),
                Paths = sandbox.Paths,
                ExistingDatabases = existing ?? [],
                Probe = probe ?? Reachable
            });

        return (await wizard.RunAsync(), output.ToString());
    }

    private static Dictionary<string, string> DatabasesIn(string settingsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return document.RootElement.GetProperty("Databases").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    [Fact]
    public async Task PastingAUrlAndPressingEnterTwiceIsAWholeInstall()
    {
        using var sandbox = new Sandbox();
        sandbox.Install("Claude Desktop");

        var (exitCode, output) = await Run(sandbox, [Url, "", ""]);

        Assert.Equal(0, exitCode);

        // Named after the database, and the placeholder from the starter file is gone.
        Assert.Equal(new Dictionary<string, string> { ["myapp"] = Url }, DatabasesIn(sandbox.Settings));

        using var client = JsonDocument.Parse(File.ReadAllText(sandbox.ClaudeDesktopConfig));
        Assert.Equal(
            Path.Combine(sandbox.App, "PostgresMcpServer.exe"),
            client.RootElement.GetProperty("mcpServers").GetProperty("postgres").GetProperty("command").GetString());

        Assert.Contains("[ OK ]", output);
    }

    [Fact]
    public async Task SavedSettingsAreOnesTheServerCanLoad()
    {
        using var sandbox = new Sandbox();

        await Run(sandbox, [Url, "", ""]);

        var (configuration, _) = ConfigurationLoader.Load(sandbox.App, databaseUrl: null);
        var connection = configuration["Databases:myapp"];

        Assert.Equal("db.example.com", ConnectionStrings.Parse(connection).Host);
        Assert.Equal("High", configuration["Safety:ConfirmAtRiskLevel"]);
        Assert.Equal("audit.log", configuration["Audit:LogPath"]);
    }

    [Fact]
    public async Task AnsweringOneQuestionAtATimeBuildsTheConnection()
    {
        using var sandbox = new Sandbox();

        var (exitCode, _) = await Run(sandbox,
            ["", "db.internal", "not-a-port", "6432", "sales", "reporter", "p;ss'word", "reports", "n"]);

        Assert.Equal(0, exitCode);

        var saved = new NpgsqlConnectionStringBuilder(DatabasesIn(sandbox.Settings)["reports"]);
        Assert.Equal("db.internal", saved.Host);
        Assert.Equal(6432, saved.Port);
        Assert.Equal("sales", saved.Database);
        Assert.Equal("reporter", saved.Username);
        Assert.Equal("p;ss'word", saved.Password);
    }

    [Fact]
    public async Task AFailedConnectionCanBeAbandonedWithoutSavingAnything()
    {
        using var sandbox = new Sandbox();
        sandbox.Install("Claude Desktop");

        var (exitCode, output) = await Run(sandbox, [Url, "q"], Unreachable);

        Assert.Equal(1, exitCode);
        Assert.Contains("password authentication failed", output);
        Assert.False(File.Exists(sandbox.Settings));
        Assert.False(File.Exists(sandbox.ClaudeDesktopConfig));
    }

    [Fact]
    public async Task AFailedConnectionCanBeRetried()
    {
        using var sandbox = new Sandbox();
        var attempts = 0;

        var (exitCode, _) = await Run(sandbox, ["postgres://me:wrong@host/db", "", Url, "", ""],
            _ => Task.FromResult(++attempts == 1
                ? new ProbeResult(false, "target", "password authentication failed")
                : new ProbeResult(true, "target", "Connected")));

        Assert.Equal(0, exitCode);
        Assert.Equal(new Dictionary<string, string> { ["myapp"] = Url }, DatabasesIn(sandbox.Settings));
    }

    [Fact]
    public async Task AnExistingFileKeepsItsOtherSettingsAndABackup()
    {
        using var sandbox = new Sandbox();
        const string original = """
            {
              "Databases": { "analytics": "Host=a;Database=b;Username=c" },
              "Limits": { "MaxRows": 25 }
            }
            """;
        File.WriteAllText(sandbox.Settings, original);

        // "y" to add a connection beside the one that already works.
        var (exitCode, _) = await Run(sandbox, ["y", Url, "", ""],
            existing: new() { ["analytics"] = "Host=a;Database=b;Username=c" });

        Assert.Equal(0, exitCode);
        Assert.Equal(["analytics", "myapp"], DatabasesIn(sandbox.Settings).Keys.Order());

        using var document = JsonDocument.Parse(File.ReadAllText(sandbox.Settings));
        Assert.Equal(25, document.RootElement.GetProperty("Limits").GetProperty("MaxRows").GetInt32());
        Assert.Equal(original, File.ReadAllText(sandbox.Settings + ".bak"));
    }

    [Fact]
    public async Task PressingEnterOnTheSuggestedNameDoesNotOverwriteAnExistingConnection()
    {
        using var sandbox = new Sandbox();
        const string working = "Host=a;Database=myapp;Username=c";
        File.WriteAllText(sandbox.Settings, $$"""{ "Databases": { "myapp": "{{working}}" } }""");

        // "y" to add, the URL, Enter on the suggested "myapp", Enter declines replacing it,
        // then a different name.
        var (exitCode, _) = await Run(sandbox, ["y", Url, "", "", "myapp_replica"],
            existing: new() { ["myapp"] = working });

        Assert.Equal(0, exitCode);
        Assert.Equal(
            new Dictionary<string, string> { ["myapp"] = working, ["myapp_replica"] = Url },
            DatabasesIn(sandbox.Settings));
    }

    [Fact]
    public async Task AWorkingSetupCanGoStraightToRegisteringAClient()
    {
        using var sandbox = new Sandbox();
        sandbox.Install("Cursor");

        // Enter declines adding a connection, Enter again registers with everything found.
        var (exitCode, _) = await Run(sandbox, ["", ""],
            existing: new() { ["local"] = "Host=localhost;Database=d;Username=u" });

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(sandbox.Settings));
        Assert.True(File.Exists(Path.Combine(sandbox.Home, ".cursor", "mcp.json")));
    }

    [Fact]
    public async Task OnlyTheChosenClientsAreTouched()
    {
        using var sandbox = new Sandbox();
        sandbox.Install("Claude Desktop");
        sandbox.Install("Claude Code");
        sandbox.Install("Cursor");

        // Claude Desktop is 1, Claude Code 2, Cursor 3.
        var (_, output) = await Run(sandbox, [Url, "", "2, 3"]);

        Assert.False(File.Exists(sandbox.ClaudeDesktopConfig));
        Assert.True(File.Exists(Path.Combine(sandbox.Home, ".cursor", "mcp.json")));
        Assert.Contains("claude mcp add postgres --scope user", output);
    }

    [Fact]
    public async Task WithNoClientInstalledThePasteableBlockCarriesTheRealPath()
    {
        using var sandbox = new Sandbox();

        var (exitCode, output) = await Run(sandbox, [Url, ""]);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"mcpServers\"", output);
        // As it must appear inside JSON: with the backslashes of a Windows path doubled.
        Assert.Contains(
            JsonSerializer.Serialize(Path.Combine(sandbox.App, "PostgresMcpServer.exe"), StarterConfiguration.Indented),
            output);
    }

    [Fact]
    public async Task InputEndingEarlyStopsCleanly()
    {
        using var sandbox = new Sandbox();

        var (exitCode, output) = await Run(sandbox, []);

        Assert.Equal(1, exitCode);
        Assert.Contains("input ended", output);
        Assert.False(File.Exists(sandbox.Settings));
    }

    [Fact]
    public async Task SomethingThatIsNotAUrlIsAskedForAgain()
    {
        using var sandbox = new Sandbox();

        var (exitCode, output) = await Run(sandbox, ["localhost", "postgres://me:pw@host/db?pgbouncer=true", Url, "", ""]);

        Assert.Equal(0, exitCode);
        Assert.Contains("does not start with postgres://", output);
        Assert.Contains("pgbouncer", output);
        Assert.Equal(new Dictionary<string, string> { ["myapp"] = Url }, DatabasesIn(sandbox.Settings));
    }
}
