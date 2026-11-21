using Npgsql;
using PostgresMcpServer.Services;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PostgresMcpServer.Setup;

public sealed class SetupOptions
{
    /// <summary>Where appsettings.json lives: the directory holding the executable.</summary>
    public required string BaseDirectory { get; init; }

    /// <summary>The absolute path clients are told to launch.</summary>
    public required string ExecutablePath { get; init; }

    public required SetupPaths Paths { get; init; }

    /// <summary>Connections already in effect, from the file or the environment.</summary>
    public IReadOnlyDictionary<string, string> ExistingDatabases { get; init; } = new Dictionary<string, string>();

    public Func<string, Task<ProbeResult>> Probe { get; init; } = connection => ConnectionProbe.ProbeAsync(connection);

    /// <summary>Reads a password without echoing it. Null reads a plain line instead.</summary>
    public Func<string?>? ReadSecret { get; init; }
}

/// <summary>
/// The guided first run: asks for a connection, proves it works, saves it, and registers the
/// server with whichever AI clients are installed. It replaces hand-editing two JSON files.
/// </summary>
public sealed partial class SetupWizard(TextReader input, TextWriter output, SetupOptions options)
{
    private const string Repository = "https://github.com/EAX3010/Postgres-mcp";

    private sealed class InputEndedException : Exception;

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ValidName();

    private string SettingsPath => Path.Combine(options.BaseDirectory, ConfigurationLoader.FileName);

    public async Task<int> RunAsync()
    {
        try
        {
            return await RunCoreAsync();
        }
        catch (InputEndedException)
        {
            output.WriteLine();
            output.WriteLine("Setup stopped because the input ended. Nothing further was changed.");
            return 1;
        }
    }

    private async Task<int> RunCoreAsync()
    {
        output.WriteLine("PostgresMcpServer setup");
        output.WriteLine(new string('-', 60));
        output.WriteLine("Connects this server to your PostgreSQL database and registers it with your");
        output.WriteLine("AI client. Press Enter to accept a value shown in [brackets].");
        output.WriteLine();
        output.WriteLine("1. Database");
        output.WriteLine();

        if (!await ConfigureDatabaseAsync()) return 1;

        output.WriteLine();
        output.WriteLine("2. AI client");
        output.WriteLine();

        RegisterClients();

        output.WriteLine();
        output.WriteLine("Done. Quit your AI client completely and reopen it, then ask it to");
        output.WriteLine("\"list the databases\".");
        output.WriteLine();
        output.WriteLine("Before pointing this at a production database, connect as a least-privilege");
        output.WriteLine($"role rather than as postgres: {Repository}/blob/main/docs/security.md");
        return 0;
    }

    // ---------- step 1: the database ----------

    private async Task<bool> ConfigureDatabaseAsync()
    {
        if (options.ExistingDatabases.Count > 0)
        {
            output.WriteLine("   Already configured:");
            var allReachable = true;

            foreach (var (name, connection) in options.ExistingDatabases)
            {
                var probe = await options.Probe(connection);
                allReachable &= probe.Success;
                output.WriteLine($"     {(probe.Success ? "[ OK ]" : "[FAIL]")} {name}  {probe.Target}");
                if (!probe.Success) output.WriteLine($"            {probe.Message}");
            }

            output.WriteLine();

            // When something is broken the likely reason for running setup is to fix it.
            if (!Confirm("   Add or replace a connection?", defaultYes: !allReachable)) return true;
            output.WriteLine();
        }

        while (true)
        {
            var connection = AskForConnection();

            output.WriteLine();
            output.WriteLine("   Testing the connection...");
            var probe = await options.Probe(connection);

            if (probe.Success)
            {
                output.WriteLine($"   [ OK ] {probe.Message}");
            }
            else
            {
                output.WriteLine($"   [FAIL] {probe.Target}");
                output.WriteLine($"          {probe.Message}");
                output.WriteLine();

                var choice = Ask("   [R]etry with different details, [S]ave it anyway, or [Q]uit?", "R").ToUpperInvariant();

                if (choice.StartsWith('Q'))
                {
                    output.WriteLine("   Nothing was saved.");
                    return false;
                }

                if (!choice.StartsWith('S'))
                {
                    output.WriteLine();
                    continue;
                }
            }

            output.WriteLine();
            var name = AskForName(connection);
            return Save(name, connection);
        }
    }

    private string AskForConnection()
    {
        output.WriteLine("   Paste a connection URL (postgres://user:password@host:5432/database),");
        output.WriteLine("   or press Enter to answer one question at a time.");

        while (true)
        {
            var pasted = Ask("   URL");

            if (pasted.Length == 0) return AskForParts();

            // A keyword string is welcome here too; anything else is most likely a typo.
            if (!ConnectionStrings.IsUrl(pasted) && !pasted.Contains('='))
            {
                output.WriteLine("   That does not start with postgres:// or postgresql://");
                continue;
            }

            try
            {
                ConnectionStrings.Parse(pasted);
                return pasted;
            }
            catch (Exception ex)
            {
                output.WriteLine($"   {ex.Message}");
            }
        }
    }

    private string AskForParts()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Ask("   Host", "localhost")
        };

        while (true)
        {
            if (int.TryParse(Ask("   Port", "5432"), out var port) && port is >= 1 and <= 65535)
            {
                builder.Port = port;
                break;
            }

            output.WriteLine("   The port is a number between 1 and 65535.");
        }

        builder.Database = Ask("   Database", "postgres");
        builder.Username = Ask("   Username", "postgres");

        output.Write("   Password: ");
        var password = options.ReadSecret is null ? ReadLine() : options.ReadSecret() ?? throw new InputEndedException();
        if (password.Length > 0) builder.Password = password;

        return builder.ConnectionString;
    }

    private string AskForName(string connection)
    {
        // The database's own name is what people already call it.
        var database = ConnectionStrings.Parse(connection).Database ?? string.Empty;
        var suggestion = ValidName().IsMatch(database) ? database : StarterConfiguration.PlaceholderName;

        output.WriteLine("   Give the connection a name. It is what you call it when talking to the");
        output.WriteLine("   assistant, as in \"query the " + suggestion + " database\".");

        while (true)
        {
            var name = Ask("   Name", suggestion);

            if (!ValidName().IsMatch(name))
            {
                output.WriteLine("   Use letters, digits, hyphens and underscores only.");
                continue;
            }

            // Pressing Enter on the suggestion must not quietly overwrite a working connection.
            if (!options.ExistingDatabases.ContainsKey(name)
                || Confirm($"   A connection named \"{name}\" already exists. Replace it?", defaultYes: false))
            {
                return name;
            }
        }
    }

    /// <summary>
    /// Adds the connection to appsettings.json, keeping every other setting in an existing
    /// file. The previous version is kept beside it, because rewriting drops any comments.
    /// </summary>
    private bool Save(string name, string connection)
    {
        var existed = File.Exists(SettingsPath);
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(
                existed ? File.ReadAllText(SettingsPath) : StarterConfiguration.Json,
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is not JsonObject document)
        {
            output.WriteLine($"   {SettingsPath} exists but is not valid JSON, so it was left alone.");
            output.WriteLine("   Fix or delete it, then run setup again.");
            return false;
        }

        if (document["Databases"] is not JsonObject databases)
        {
            databases = new JsonObject();
            document["Databases"] = databases;
        }

        // The untouched --init placeholder can never connect and would fail every --check.
        if (databases[StarterConfiguration.PlaceholderName] is JsonValue placeholder
            && placeholder.TryGetValue<string>(out var value)
            && value == StarterConfiguration.PlaceholderConnection)
        {
            databases.Remove(StarterConfiguration.PlaceholderName);
        }

        databases[name] = connection;

        if (existed) File.Copy(SettingsPath, SettingsPath + ClientConfigFile.BackupSuffix, overwrite: true);
        StarterConfiguration.WriteSettings(SettingsPath, document.ToJsonString(StarterConfiguration.Indented) + Environment.NewLine);

        output.WriteLine();
        output.WriteLine($"   Saved to {SettingsPath}");
        return true;
    }

    // ---------- step 2: the clients ----------

    private void RegisterClients()
    {
        var installed = McpClient.All.Where(c => c.IsInstalled(options.Paths)).ToList();

        if (installed.Count == 0)
        {
            output.WriteLine("   No supported AI client was found in its usual place. Add this to your");
            output.WriteLine("   client's MCP configuration:");
            output.WriteLine();
            WriteIndented(McpClient.All[0].ManualBlock(options.ExecutablePath));
            output.WriteLine();
            output.WriteLine($"   Where each client keeps that file: {Repository}/tree/main/docs/clients");
            return;
        }

        output.WriteLine("   Found on this computer:");
        for (var i = 0; i < installed.Count; i++)
            output.WriteLine($"     {i + 1}. {installed[i].Name}");
        output.WriteLine();

        var chosen = ChooseClients(installed);
        if (chosen.Count == 0)
        {
            output.WriteLine("   Skipped. The path to give a client later is:");
            output.WriteLine($"     {options.ExecutablePath}");
            return;
        }

        foreach (var client in chosen)
        {
            output.WriteLine();
            Register(client);
        }
    }

    private List<McpClient> ChooseClients(List<McpClient> installed)
    {
        while (true)
        {
            var answer = Ask("   Register with which? Numbers separated by commas, A for all, N for none", "A");

            if (answer.StartsWith("a", StringComparison.OrdinalIgnoreCase)) return installed;
            if (answer.StartsWith("n", StringComparison.OrdinalIgnoreCase)) return [];

            var numbers = answer.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (numbers.All(n => int.TryParse(n, out var i) && i >= 1 && i <= installed.Count))
                return numbers.Select(n => installed[int.Parse(n) - 1]).Distinct().ToList();

            output.WriteLine($"   Enter numbers between 1 and {installed.Count}.");
        }
    }

    private void Register(McpClient client)
    {
        if (client.Command is not null)
        {
            output.WriteLine($"   {client.Name}: run this command");
            output.WriteLine($"     {client.Command(options.ExecutablePath)}");
            return;
        }

        var path = client.ConfigPath(options.Paths)!;
        Registration result;

        try
        {
            result = ClientConfigFile.Register(path, client.ServersKey, client.NewEntry(options.ExecutablePath));

            if (result.Outcome == RegistrationOutcome.Conflict
                && Confirm($"   {client.Name} already has a different server named \"{ClientConfigFile.ServerName}\" ({result.Detail}). Replace it?", defaultYes: false))
            {
                result = ClientConfigFile.Register(path, client.ServersKey, client.NewEntry(options.ExecutablePath), replaceOtherServer: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"   {client.Name}: could not write {path}");
            output.WriteLine($"     {ex.Message}");
            return;
        }

        switch (result.Outcome)
        {
            case RegistrationOutcome.Created:
                output.WriteLine($"   {client.Name}: registered. Created {path}");
                break;

            case RegistrationOutcome.Added:
            case RegistrationOutcome.Updated:
                output.WriteLine($"   {client.Name}: registered in {path}");
                output.WriteLine($"     The previous version is beside it, ending in {ClientConfigFile.BackupSuffix}");
                break;

            case RegistrationOutcome.Unchanged:
                output.WriteLine($"   {client.Name}: already registered.");
                break;

            case RegistrationOutcome.Conflict:
                output.WriteLine($"   {client.Name}: left as it was.");
                break;

            case RegistrationOutcome.NeedsManualEdit:
                output.WriteLine($"   {client.Name}: {path}");
                output.WriteLine($"     {result.Detail}, so it was not changed. Add this to it by hand:");
                output.WriteLine();
                WriteIndented(client.ManualBlock(options.ExecutablePath));
                break;
        }
    }

    // ---------- prompts ----------

    private void WriteIndented(string block)
    {
        foreach (var line in block.Split('\n'))
            output.WriteLine("     " + line.TrimEnd('\r'));
    }

    private string Ask(string prompt, string? fallback = null)
    {
        output.Write(fallback is null ? $"{prompt}: " : $"{prompt} [{fallback}]: ");
        var answer = ReadLine().Trim();
        return answer.Length == 0 && fallback is not null ? fallback : answer;
    }

    private bool Confirm(string prompt, bool defaultYes)
    {
        output.Write($"{prompt} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        var answer = ReadLine().Trim();
        return answer.Length == 0 ? defaultYes : answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private string ReadLine() => input.ReadLine() ?? throw new InputEndedException();
}
