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
        var parsed = PostgresUriParser.TryParse(text)
                     ?? throw new FormatException($"Cannot parse PostgreSQL URI: {text}");

        var sslMode = ESslMode.Prefer;
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in parsed.QueryParams)
        {
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                sslMode = ParseSslMode(value);
            }
            else
            {
                options[key] = value;
            }
        }

        var connection = new DatabaseConnection
                             {
                                 Provider = EDatabaseProvider.PostgreSql,
                                 Host = parsed.Host,
                                 Port = parsed.Port,
                                 Database = parsed.Database,
                                 Username = parsed.Username ?? "postgres",
                                 Password = parsed.Password,
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
