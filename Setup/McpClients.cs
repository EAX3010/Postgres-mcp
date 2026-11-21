using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgresMcpServer.Setup;

public enum HostPlatform { Windows, MacOS, Linux }

/// <summary>Where client configuration lives on this machine. Passed in so tests can redirect it.</summary>
public sealed record SetupPaths(HostPlatform Platform, string Home, string AppData)
{
    public static SetupPaths Current() => new(
        OperatingSystem.IsWindows() ? HostPlatform.Windows
            : OperatingSystem.IsMacOS() ? HostPlatform.MacOS
            : HostPlatform.Linux,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        // Roaming on Windows, ~/Library/Application Support on macOS, ~/.config on Linux.
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}

/// <summary>One MCP client, and how this server is registered with it.</summary>
public sealed class McpClient
{
    public required string Name { get; init; }

    /// <summary>Clients disagree on this: VS Code uses servers, Zed uses context_servers.</summary>
    public string ServersKey { get; init; } = "mcpServers";

    /// <summary>
    /// A directory that exists only when the client is installed. Null when the client is
    /// not available on the platform.
    /// </summary>
    public required Func<SetupPaths, string?> InstallDirectory { get; init; }

    /// <summary>The file to edit, relative to <see cref="InstallDirectory"/>.</summary>
    public string? ConfigFile { get; init; }

    /// <summary>For a client that is configured by its own command rather than a file.</summary>
    public Func<string, string>? Command { get; init; }

    public Func<string, JsonObject> NewEntry { get; init; } =
        executable => new JsonObject { ["command"] = executable };

    public bool IsInstalled(SetupPaths paths) =>
        InstallDirectory(paths) is { } directory && Directory.Exists(directory);

    public string? ConfigPath(SetupPaths paths) =>
        ConfigFile is not null && InstallDirectory(paths) is { } directory
            ? Path.Combine(directory, ConfigFile)
            : null;

    /// <summary>The block a user would paste by hand, with the real path already in it.</summary>
    public string ManualBlock(string executable) =>
        new JsonObject
        {
            [ServersKey] = new JsonObject { [ClientConfigFile.ServerName] = NewEntry(executable) }
        }.ToJsonString(StarterConfiguration.Indented);

    public static readonly IReadOnlyList<McpClient> All =
    [
        new()
        {
            Name = "Claude Desktop",
            InstallDirectory = p => p.Platform == HostPlatform.Linux ? null : Path.Combine(p.AppData, "Claude"),
            ConfigFile = "claude_desktop_config.json"
        },
        new()
        {
            // Its own file is large and rewritten constantly by the client, so the supported
            // route is the command rather than an edit.
            Name = "Claude Code",
            InstallDirectory = p => Path.Combine(p.Home, ".claude"),
            Command = executable => $"claude mcp add {ClientConfigFile.ServerName} --scope user \"{executable}\""
        },
        new()
        {
            Name = "Cursor",
            InstallDirectory = p => Path.Combine(p.Home, ".cursor"),
            ConfigFile = "mcp.json"
        },
        new()
        {
            Name = "VS Code",
            ServersKey = "servers",
            InstallDirectory = p => Path.Combine(p.AppData, "Code", "User"),
            ConfigFile = "mcp.json",
            NewEntry = executable => new JsonObject { ["type"] = "stdio", ["command"] = executable }
        },
        new()
        {
            Name = "Windsurf",
            InstallDirectory = p => Path.Combine(p.Home, ".codeium", "windsurf"),
            ConfigFile = "mcp_config.json"
        },
        new()
        {
            Name = "Cline",
            InstallDirectory = p => Path.Combine(p.AppData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev"),
            ConfigFile = Path.Combine("settings", "cline_mcp_settings.json"),
            // autoApprove stays empty: approving a tool that can DROP TABLE without asking
            // removes the confirmation step this server is built around.
            NewEntry = executable => new JsonObject
            {
                ["command"] = executable,
                ["disabled"] = false,
                ["autoApprove"] = new JsonArray()
            }
        },
        new()
        {
            Name = "Gemini CLI",
            InstallDirectory = p => Path.Combine(p.Home, ".gemini"),
            ConfigFile = "settings.json"
        },
        new()
        {
            Name = "Zed",
            ServersKey = "context_servers",
            InstallDirectory = p => p.Platform == HostPlatform.Windows
                ? Path.Combine(p.AppData, "Zed")
                : Path.Combine(p.Home, ".config", "zed"),
            ConfigFile = "settings.json",
            NewEntry = executable => new JsonObject
            {
                ["source"] = "custom",
                ["command"] = executable,
                ["args"] = new JsonArray(),
                ["env"] = new JsonObject()
            }
        }
    ];
}

public enum RegistrationOutcome
{
    /// <summary>The file did not exist and was created.</summary>
    Created,

    /// <summary>The server was added to an existing file.</summary>
    Added,

    /// <summary>An earlier registration of this server now points at the new path.</summary>
    Updated,

    /// <summary>Already registered with this exact path; nothing was written.</summary>
    Unchanged,

    /// <summary>A different server already uses the name; nothing was written.</summary>
    Conflict,

    /// <summary>The file cannot be rewritten without losing something; nothing was written.</summary>
    NeedsManualEdit
}

/// <param name="Detail">The other server's command for a conflict, or why a manual edit is needed.</param>
public sealed record Registration(RegistrationOutcome Outcome, string Detail = "");

/// <summary>
/// Adds this server to a client's JSON configuration, leaving everything else in the file as
/// it was. Hand-editing that file - and in particular doubling the backslashes in a Windows
/// path - is where most installations went wrong.
/// </summary>
public static class ClientConfigFile
{
    public const string ServerName = "postgres";
    public const string BackupSuffix = ".bak";

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static Registration Register(string path, string serversKey, JsonObject entry, bool replaceOtherServer = false)
    {
        var existed = File.Exists(path);
        var text = existed ? File.ReadAllText(path) : string.Empty;

        JsonNode? root;
        if (string.IsNullOrWhiteSpace(text))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // Several editors keep comments in their settings. Writing the file back
                // would silently delete them, so it is left for the user to edit.
                return new Registration(RegistrationOutcome.NeedsManualEdit,
                    IsLenientJson(text)
                        ? "contains comments or trailing commas, which rewriting it would remove"
                        : "is not valid JSON");
            }
        }

        if (root is not JsonObject document)
            return new Registration(RegistrationOutcome.NeedsManualEdit, "does not hold a JSON object");

        if (document[serversKey] is null) document[serversKey] = new JsonObject();

        if (document[serversKey] is not JsonObject servers)
            return new Registration(RegistrationOutcome.NeedsManualEdit, $"has a \"{serversKey}\" entry that is not an object");

        var command = entry["command"]!.GetValue<string>();
        RegistrationOutcome outcome;

        if (servers[ServerName] is null)
        {
            servers[ServerName] = entry;
            outcome = existed ? RegistrationOutcome.Added : RegistrationOutcome.Created;
        }
        else if (servers[ServerName] is JsonObject current && CommandOf(current) is { } currentCommand && IsThisServer(currentCommand))
        {
            if (currentCommand == command) return new Registration(RegistrationOutcome.Unchanged);

            // Only the path moves; anything the user added to the entry, such as an env block, stays.
            current["command"] = command;
            outcome = RegistrationOutcome.Updated;
        }
        else if (replaceOtherServer)
        {
            servers[ServerName] = entry;
            outcome = RegistrationOutcome.Updated;
        }
        else
        {
            var other = servers[ServerName] is JsonObject o ? CommandOf(o) : null;
            return new Registration(RegistrationOutcome.Conflict, other is null ? "unknown" : FileName(other));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (existed) File.Copy(path, path + BackupSuffix, overwrite: true);
        File.WriteAllText(path, document.ToJsonString(StarterConfiguration.Indented) + Environment.NewLine);

        return new Registration(outcome);
    }

    private static string? CommandOf(JsonObject server) =>
        server["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;

    /// <summary>True when an existing entry is an earlier install of this server, wherever it lived.</summary>
    private static bool IsThisServer(string command)
    {
        var name = FileName(command);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Equals("PostgresMcpServer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Path.GetFileName only splits on the running platform's separator.</summary>
    private static string FileName(string command) =>
        command[(command.LastIndexOfAny(['/', '\\']) + 1)..];

    private static bool IsLenientJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text, Lenient);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
