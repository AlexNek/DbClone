using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// Environment variable format: DATABASE_URL=postgresql://user:pass@host:port/db
/// Import only. Strips the variable name and delegates to URI or key-value parsing.
/// </summary>
public sealed class PostgreSqlEnvVarFormat : IConnectionTextFormat
{
    private static readonly string[] KnownVarNames =
        [
            "DATABASE_URL=",
            "DB_URL=",
            "POSTGRES_URL=",
            "PG_URL=",
            "PGHOST=",
            "PGPORT=",
            "PGDATABASE=",
            "PGUSER=",
            "PGPASSWORD="
        ];

    private readonly PostgreSqlNpgsqlFormat _npgsqlFormat = new();

    private readonly PostgreSqlUriFormat _uriFormat = new();

    public int DetectionPriority => 50;

    public string DisplayName => "Environment Variable";

    public string Id => "pg-envvar";

    public string Provider => "postgresql";

    public string TypicalSource => "Docker / .env / CI/CD";

    public bool CanExport(ConnectionStringFields fields) => false; // import only

    public bool CanImport(string text) =>
        KnownVarNames.Any(v => text.StartsWith(v, StringComparison.OrdinalIgnoreCase));

    public string Export(ConnectionStringFields fields) =>
        throw new NotSupportedException("Environment Variable format is import-only.");

    public ConnectionStringFields Parse(string text)
    {
        // Handle DATABASE_URL=postgresql://... style
        var urlVarNames = new[] { "DATABASE_URL=", "DB_URL=", "POSTGRES_URL=", "PG_URL=" };
        foreach (var varName in urlVarNames)
        {
            if (text.StartsWith(varName, StringComparison.OrdinalIgnoreCase))
            {
                var value = text[varName.Length..].Trim().Trim('"', '\'');
                return _uriFormat.Parse(value);
            }
        }

        // Handle individual PG* env vars (multi-line or single line)
        var envVars = ParseEnvVars(text);
        var host = envVars.GetValueOrDefault("PGHOST", "localhost");
        var port = int.TryParse(envVars.GetValueOrDefault("PGPORT"), out var p) ? p : 5432;
        var database = envVars.GetValueOrDefault("PGDATABASE", string.Empty);
        var username = envVars.GetValueOrDefault("PGUSER", "postgres");
        var password = envVars.GetValueOrDefault("PGPASSWORD");

        return new ConnectionStringFields(
            Host: host,
            Port: port,
            Database: database,
            Username: username,
            Password: password ?? string.Empty,
            SslMode: ESslMode.Prefer,
            Provider: "postgresql");
    }

    private static Dictionary<string, string> ParseEnvVars(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim().Trim('"', '\'');

            if (key.StartsWith("PG", StringComparison.OrdinalIgnoreCase))
                result[key] = value;
        }

        return result;
    }
}
