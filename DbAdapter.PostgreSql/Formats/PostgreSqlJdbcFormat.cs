using System.Web;

using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// JDBC format: jdbc:postgresql://host:port/db?user=x&amp;password=y
/// </summary>
public sealed class PostgreSqlJdbcFormat : IConnectionTextFormat
{
    private const string Prefix = "jdbc:postgresql://";

    private const string DefaultHost = "localhost";

    private readonly int _defaultPort;

    /// <summary>
    /// Initializes a new instance. <paramref name="defaultPort"/> is the engine default as
    /// the platform data defines it (defaults.port of the base engine file).
    /// </summary>
    public PostgreSqlJdbcFormat(int defaultPort) => _defaultPort = defaultPort;

    public int DetectionPriority => 20;

    public string DisplayName => "JDBC";

    public string Id => "pg-jdbc";

    public string Provider => "postgresql";

    public string TypicalSource => "Java / Kotlin / Scala";

    public bool CanExport(ConnectionStringFields fields) =>
        fields.Provider == "postgresql";

    public bool CanImport(string text) =>
        text.StartsWith("jdbc:postgresql://", StringComparison.OrdinalIgnoreCase);

    public string Export(ConnectionStringFields fields)
    {
        var sslParam = fields.SslMode != ESslMode.Prefer
                           ? $"&sslmode={PostgreSqlUriFormat.FormatSslMode(fields.SslMode)}"
                           : string.Empty;

        var passwordParam = !string.IsNullOrEmpty(fields.Password)
                                ? $"&password={Uri.EscapeDataString(fields.Password)}"
                                : string.Empty;

        return $"jdbc:postgresql://{fields.Host}:{fields.Port}/{fields.Database}" +
               $"?user={Uri.EscapeDataString(fields.Username)}{passwordParam}{sslParam}";
    }

    public ConnectionStringFields Parse(string text)
    {
        // Strip "jdbc:" prefix and parse as standard URI
        var uriPart = text["jdbc:".Length..]; // "postgresql://host:port/db?params"
        var uri = new Uri(uriPart);

        var hostRaw = uri.Host;
        var hasHost = !string.IsNullOrEmpty(hostRaw);
        var host = hasHost ? hostRaw : DefaultHost;
        var hasPort = uri.Port > 0;
        var port = hasPort ? uri.Port : _defaultPort;

        var database = string.Empty;
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            database = uri.AbsolutePath.TrimStart('/');

        var username = "postgres";
        string? password = null;

        // JDBC typically passes user/password as query params
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sslMode = ESslMode.Prefer;

        if (!string.IsNullOrEmpty(uri.Query))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            foreach (string? key in query)
            {
                if (key is null) continue;
                var value = query[key] ?? string.Empty;

                if (key.Equals("user", StringComparison.OrdinalIgnoreCase))
                    username = value;
                else if (key.Equals("password", StringComparison.OrdinalIgnoreCase))
                    password = value;
                else if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                    sslMode = PostgreSqlUriFormat.ParseSslMode(value);
                else if (key.Equals("ssl", StringComparison.OrdinalIgnoreCase))
                    sslMode = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                                  ? ESslMode.Require
                                  : ESslMode.Disable;
                else
                    options[key] = value;
            }
        }

        // Also support userinfo in URI (less common for JDBC but valid)
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
                password = Uri.UnescapeDataString(parts[1]);
        }

        // A JDBC URL with no port took the engine default, and one whose authority names no
        // host took the local server; record either so the import service can say so.
        if (!hasPort)
            options["_defaulted:Port"] = _defaultPort.ToString();
        if (!hasHost)
            options["_defaulted:Host"] = DefaultHost;

        return new ConnectionStringFields(
            Host: host,
            Port: port,
            Database: database,
            Username: username,
            Password: password ?? string.Empty,
            SslMode: sslMode,
            Provider: "postgresql",
            Options: options);
    }
}
