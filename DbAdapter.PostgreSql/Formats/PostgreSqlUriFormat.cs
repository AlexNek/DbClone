using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// PostgreSQL URI format: postgresql://user:pass@host:port/db?sslmode=require
/// </summary>
public sealed class PostgreSqlUriFormat : IConnectionTextFormat
{
    public int DetectionPriority => 10;

    public string DisplayName => "PostgreSQL URI";

    public string Id => "pg-uri";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "General / CLI / Cloud platforms";

    public bool CanExport(ConnectionStringFields fields) =>
        fields.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text) =>
        text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    public string Export(ConnectionStringFields fields)
    {
        var userInfo = string.IsNullOrEmpty(fields.Password)
                           ? Uri.EscapeDataString(fields.Username)
                           : $"{Uri.EscapeDataString(fields.Username)}:{Uri.EscapeDataString(fields.Password)}";

        var sslParam = fields.SslMode != ESslMode.Prefer
                           ? $"?sslmode={FormatSslMode(fields.SslMode)}"
                           : string.Empty;

        return
            $"postgresql://{userInfo}@{fields.Host}:{fields.Port}/{fields.Database}{sslParam}";
    }

    public ConnectionStringFields Parse(string text)
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

        return new ConnectionStringFields(
            Host: parsed.Host,
            Port: parsed.Port,
            Database: parsed.Database,
            Username: parsed.Username ?? "postgres",
            Password: parsed.Password ?? string.Empty,
            SslMode: sslMode,
            Provider: EDatabaseProvider.PostgreSql,
            Options: options);
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
