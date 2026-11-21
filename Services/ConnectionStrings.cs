using Npgsql;

namespace PostgresMcpServer.Services;

/// <summary>
/// A database may be configured either as an Npgsql keyword string
/// (<c>Host=...;Database=...</c>) or as the <c>postgres://</c> URL that hosting dashboards and
/// <c>.env</c> files hand out. Npgsql itself only understands the first form, so URLs are
/// translated here.
/// </summary>
public static class ConnectionStrings
{
    private static readonly string[] UrlSchemes = ["postgresql://", "postgres://"];

    /// <summary>libpq URL parameter names and the Npgsql keyword each corresponds to.</summary>
    private static readonly Dictionary<string, string> UrlParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sslmode"] = "SSL Mode",
        ["sslrootcert"] = "Root Certificate",
        ["sslcert"] = "SSL Certificate",
        ["sslkey"] = "SSL Key",
        ["sslpassword"] = "SSL Password",
        ["channel_binding"] = "Channel Binding",
        ["application_name"] = "Application Name",
        ["connect_timeout"] = "Timeout",
        ["options"] = "Options",
        ["host"] = "Host",
        ["port"] = "Port",
        ["dbname"] = "Database",
        ["user"] = "Username",
        ["password"] = "Password"
    };

    public static bool IsUrl(string? value) =>
        value is not null && UrlSchemes.Any(s => value.TrimStart().StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns a string Npgsql accepts. A keyword string is passed through untouched, so
    /// existing configurations behave exactly as before.
    /// </summary>
    public static string Normalize(string? value) =>
        IsUrl(value) ? FromUrl(value!).ConnectionString : value ?? string.Empty;

    /// <summary>Parses either form. Throws when the value is neither.</summary>
    public static NpgsqlConnectionStringBuilder Parse(string? value) =>
        IsUrl(value) ? FromUrl(value!) : new NpgsqlConnectionStringBuilder(value ?? string.Empty);

    /// <summary>
    /// Translates <c>postgres://user:password@host:port/database?param=value</c>.
    /// Error messages never quote part of the URL: a URL that fails to parse is usually one
    /// with an unencoded password, and these messages reach logs and the AI client.
    /// </summary>
    private static NpgsqlConnectionStringBuilder FromUrl(string url)
    {
        url = url.Trim();
        var scheme = UrlSchemes.First(s => url.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        var rest = url[scheme.Length..];

        var queryStart = rest.IndexOf('?');
        var location = queryStart < 0 ? rest : rest[..queryStart];
        var query = queryStart < 0 ? string.Empty : rest[(queryStart + 1)..];

        // The last '@' ends the credentials, so a password containing '@', ':' or '/' still
        // parses without being percent-encoded. A '?' cannot be rescued the same way.
        var at = location.LastIndexOf('@');
        if (at < 0 && query.Contains('@'))
        {
            throw new FormatException(
                "The connection URL has an '@' after a '?' but none before it. If the password " +
                "contains '?', write it as %3F; if the '@' belongs to a parameter value, write it as %40.");
        }

        var credentials = at < 0 ? string.Empty : location[..at];
        var hostAndPath = at < 0 ? location : location[(at + 1)..];

        var slash = hostAndPath.IndexOf('/');
        var authority = slash < 0 ? hostAndPath : hostAndPath[..slash];
        var database = slash < 0 ? string.Empty : hostAndPath[(slash + 1)..];

        var builder = new NpgsqlConnectionStringBuilder();

        var (host, port) = SplitAuthority(authority);
        builder.Host = host.Length > 0 ? Uri.UnescapeDataString(host) : "localhost";
        if (port is not null) builder.Port = port.Value;

        if (credentials.Length > 0)
        {
            var colon = credentials.IndexOf(':');
            builder.Username = Uri.UnescapeDataString(colon < 0 ? credentials : credentials[..colon]);
            if (colon >= 0) builder.Password = Uri.UnescapeDataString(credentials[(colon + 1)..]);
        }

        if (database.Length > 0) builder.Database = Uri.UnescapeDataString(database);

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);

            // libpq spells these values verify-full and verify-ca; Npgsql has no hyphen.
            if (name.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                value = value.Replace("-", string.Empty);

            // Anything that is not a libpq name is tried as an Npgsql keyword, so
            // ?Include Error Detail=true works in a URL too.
            var keyword = UrlParameters.GetValueOrDefault(name, name);

            try
            {
                builder[keyword] = value;
            }
            catch (Exception)
            {
                throw new FormatException(
                    $"The connection URL parameter '{name}' is not one this server understands, " +
                    "or its value is not valid. Remove it, or use the Npgsql keyword instead.");
            }
        }

        return builder;
    }

    /// <summary>Splits <c>host</c>, <c>host:port</c> and <c>[ipv6]:port</c>.</summary>
    private static (string Host, int? Port) SplitAuthority(string authority)
    {
        // Several hosts for failover: Npgsql takes the comma-separated list as it stands.
        if (authority.Contains(',')) return (authority, null);

        string host;
        string portText;

        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) throw new FormatException("The connection URL has an IPv6 address with no closing ']'.");

            host = authority[1..close];
            portText = authority[(close + 1)..].TrimStart(':');
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            portText = colon < 0 ? string.Empty : authority[(colon + 1)..];
        }

        if (portText.Length == 0) return (host, null);

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new FormatException(
                "The connection URL has a port that is not a number between 1 and 65535. If the " +
                "password contains '?' or '#', percent-encode it (%3F, %23).");
        }

        return (host, port);
    }
}
