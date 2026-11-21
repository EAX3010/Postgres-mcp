using Microsoft.Extensions.Configuration;

namespace PostgresMcpServer.Services;

public enum DatabaseUrlStatus
{
    NotSet,

    /// <summary>No other database was configured, so DATABASE_URL became the only one.</summary>
    Used,

    /// <summary>Databases were configured explicitly, so DATABASE_URL was left alone.</summary>
    Ignored
}

/// <summary>
/// Builds the configuration from an optional file next to the executable plus POSTGRESMCP_*
/// environment variables, so a deployment can use either, both, or only the environment.
/// </summary>
public static class ConfigurationLoader
{
    public const string FileName = "appsettings.json";
    public const string DatabaseUrlVariable = "DATABASE_URL";

    /// <summary>The connection name DATABASE_URL is exposed under.</summary>
    public const string DatabaseUrlName = "default";

    /// <summary>
    /// DATABASE_URL is only a fallback. Developer shells often export one for the application
    /// they are working on, usually with a read-write role; adding that beside a deliberately
    /// read-only configuration would hand the assistant more access than was chosen.
    /// </summary>
    public static (IConfigurationRoot Configuration, DatabaseUrlStatus DatabaseUrl) Load(string baseDir, string? databaseUrl)
    {
        IConfigurationBuilder Sources() => new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile(FileName, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("POSTGRESMCP_");

        var configuration = Sources().Build();

        if (string.IsNullOrWhiteSpace(databaseUrl))
            return (configuration, DatabaseUrlStatus.NotSet);

        if (configuration.GetSection("Databases").GetChildren().Any())
            return (configuration, DatabaseUrlStatus.Ignored);

        configuration = Sources()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Databases:{DatabaseUrlName}"] = databaseUrl.Trim()
            })
            .Build();

        return (configuration, DatabaseUrlStatus.Used);
    }
}
