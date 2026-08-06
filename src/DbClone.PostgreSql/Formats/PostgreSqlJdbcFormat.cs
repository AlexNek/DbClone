using System.Web;

using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// JDBC format: jdbc:postgresql://host:port/db?user=x&amp;password=y
/// </summary>
public sealed class PostgreSqlJdbcFormat : IConnectionFormat
{
    private const string Prefix = "jdbc:postgresql://";

    public int DetectionPriority => 20;

    public string DisplayName => "JDBC";

    public string Id => "pg-jdbc";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "Java / Kotlin / Scala";

    public bool CanExport(DatabaseConnection connection) =>
        connection.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text) =>
        text.StartsWith("jdbc:postgresql://", StringComparison.OrdinalIgnoreCase);

    public string Export(DatabaseConnection connection)
    {
        var sslParam = connection.SslMode != ESslMode.Prefer
                           ? $"&sslmode={PostgreSqlUriFormat.FormatSslMode(connection.SslMode)}"
                           : string.Empty;

        var passwordParam = !string.IsNullOrEmpty(connection.Password)
                                ? $"&password={Uri.EscapeDataString(connection.Password)}"
                                : string.Empty;

        return $"jdbc:postgresql://{connection.Host}:{connection.Port}/{connection.Database}" +
               $"?user={Uri.EscapeDataString(connection.Username)}{passwordParam}{sslParam}";
    }

    public DatabaseConnection Parse(string text)
    {
        // Strip "jdbc:" prefix and parse as standard URI
        var uriPart = text["jdbc:".Length..]; // "postgresql://host:port/db?params"
        var uri = new Uri(uriPart);

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5432;

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

        var connection = new DatabaseConnection
                             {
                                 Provider = EDatabaseProvider.PostgreSql,
                                 Host = host,
                                 Port = port,
                                 Database = database,
                                 Username = username,
                                 Password = password,
                                 SslMode = sslMode
                             };

        foreach (var opt in options)
            connection.Options[opt.Key] = opt.Value;

        return connection;
    }
}
