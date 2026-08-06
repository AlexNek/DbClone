using System.Web;

using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// PostgreSQL URI format: postgresql://user:pass@host:port/db?sslmode=require
/// </summary>
public sealed class PostgreSqlUriFormat : IConnectionFormat
{
    public int DetectionPriority => 10;

    public string DisplayName => "PostgreSQL URI";

    public string Id => "pg-uri";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "General / CLI / Cloud platforms";

    public bool CanExport(DatabaseConnection connection) =>
        connection.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text) =>
        text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    public string Export(DatabaseConnection connection)
    {
        var userInfo = string.IsNullOrEmpty(connection.Password)
                           ? Uri.EscapeDataString(connection.Username)
                           : $"{Uri.EscapeDataString(connection.Username)}:{Uri.EscapeDataString(connection.Password)}";

        var sslParam = connection.SslMode != ESslMode.Prefer
                           ? $"?sslmode={FormatSslMode(connection.SslMode)}"
                           : string.Empty;

        return
            $"postgresql://{userInfo}@{connection.Host}:{connection.Port}/{connection.Database}{sslParam}";
    }

    public DatabaseConnection Parse(string text)
    {
        var uri = new Uri(text);

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5432;

        var database = string.Empty;
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            database = uri.AbsolutePath.TrimStart('/');

        var username = "postgres";
        string? password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
                password = Uri.UnescapeDataString(parts[1]);
        }

        var sslMode = ESslMode.Prefer;
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(uri.Query))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            foreach (string? key in query)
            {
                if (key is null) continue;
                var value = query[key] ?? string.Empty;

                if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                {
                    sslMode = ParseSslMode(value);
                }
                else
                {
                    options[key] = value;
                }
            }
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

    internal static string FormatSslMode(ESslMode mode) =>
        mode switch
            {
                ESslMode.Disable => "disable",
                ESslMode.Prefer => "prefer",
                ESslMode.Require => "require",
                ESslMode.VerifyCA => "verify-ca",
                ESslMode.VerifyFull => "verify-full",
                _ => "prefer"
            };

    internal static ESslMode ParseSslMode(string value) =>
        value.ToLowerInvariant() switch
            {
                "disable" => ESslMode.Disable,
                "prefer" => ESslMode.Prefer,
                "require" => ESslMode.Require,
                "verify-ca" => ESslMode.VerifyCA,
                "verify-full" => ESslMode.VerifyFull,
                _ => ESslMode.Prefer
            };
}
