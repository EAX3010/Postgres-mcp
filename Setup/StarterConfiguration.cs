using System.Text.Encodings.Web;
using System.Text.Json;

namespace PostgresMcpServer.Setup;

/// <summary>
/// The configuration a new installation starts from. Deliberately smaller than
/// appsettings.example.json, which documents every option across several connections and
/// would fail --check repeatedly.
/// </summary>
public static class StarterConfiguration
{
    public const string PlaceholderName = "local";

    /// <summary>What --init writes. Nobody can connect with it, which is the point.</summary>
    public const string PlaceholderConnection =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=CHANGE_ME";

    public const string Json = $$"""
        {
          "Databases": {
            "{{PlaceholderName}}": "{{PlaceholderConnection}}"
          },
          "Safety": {
            "RequireConfirmation": true,
            "EnableDryRun": true,
            "ConfirmAtRiskLevel": "High",
            "AllowMultiStatement": false
          },
          "Limits": {
            "CommandTimeoutSeconds": 30,
            "MaxRows": 1000,
            "MaxResponseBytes": 1000000
          },
          "Audit": {
            "Enabled": true,
            "LogPath": "audit.log",
            "LogToConsole": false
          }
        }

        """;

    /// <summary>
    /// Relaxed escaping keeps a password such as <c>p&amp;ss'word</c> readable in the file
    /// instead of turning it into & sequences that are awkward to edit by hand.
    /// </summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Writes a settings file that holds a password, readable by its owner only.</summary>
    public static void WriteSettings(string path, string json)
    {
        File.WriteAllText(path, json);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
