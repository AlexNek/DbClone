using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Formats;

/// <summary>
/// libpq / psql keyword=value format: host=localhost port=5432 dbname=mydb user=postgres password=secret
/// </summary>
public sealed class PostgreSqlLibpqFormat : IConnectionTextFormat
{
    public int DetectionPriority => 40;

    public string DisplayName => "libpq / psql";

    public string Id => "pg-libpq";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "C / psql / pgAdmin";

    public bool CanExport(ConnectionStringFields fields) =>
        fields.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text)
    {
        // Must NOT be a URI or JDBC string
        if (text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase))
            return false;

        // libpq keywords are lowercase and space-separated ("host=", "dbname=").
        // Case-sensitive matching ensures we don't claim ADO.NET "Host=...;..."
        // or environment-variable "PGHOST=..." strings.
        return text.Contains("host=", StringComparison.Ordinal)
               || text.Contains("dbname=", StringComparison.Ordinal);
    }

    public string Export(ConnectionStringFields fields)
    {
        var parts = new List<string>
                        {
                            $"host={fields.Host}",
                            $"port={fields.Port}",
                            $"dbname={fields.Database}",
                            $"user={fields.Username}"
                        };

        if (!string.IsNullOrEmpty(fields.Password))
            parts.Add($"password={fields.Password}");

        if (fields.SslMode != ESslMode.Prefer)
            parts.Add($"sslmode={PostgreSqlUriFormat.FormatSslMode(fields.SslMode)}");

        foreach (var opt in fields.Options ?? new Dictionary<string, string>())
            parts.Add($"{opt.Key.ToLowerInvariant()}={opt.Value}");

        return string.Join(" ", parts);
    }

    public ConnectionStringFields Parse(string text)
    {
        var pairs = ParseKeyValuePairs(text);

        var host = GetValue(pairs, "host") ?? "localhost";
        var port = int.TryParse(GetValue(pairs, "port"), out var p) ? p : 5432;
        var database = GetValue(pairs, "dbname") ?? string.Empty;
        var username = GetValue(pairs, "user") ?? "postgres";
        var password = GetValue(pairs, "password");

        var sslMode = ESslMode.Prefer;
        var sslValue = GetValue(pairs, "sslmode");
        if (sslValue is not null)
            sslMode = PostgreSqlUriFormat.ParseSslMode(sslValue);

        // Preserve remaining options
        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                "host",
                                "port",
                                "dbname",
                                "user",
                                "password",
                                "sslmode"
                            };

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            if (!knownKeys.Contains(pair.Key))
                options[pair.Key] = pair.Value;
        }

        return new ConnectionStringFields(
            Host: host,
            Port: port,
            Database: database,
            Username: username,
            Password: password ?? string.Empty,
            SslMode: sslMode,
            Provider: EDatabaseProvider.PostgreSql,
            Options: options);
    }

    private static string? GetValue(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var value) ? value : null;

    private static Dictionary<string, string> ParseKeyValuePairs(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // libpq format: key=value pairs separated by spaces
        // Values can be quoted with single quotes: password='my secret'
        var i = 0;
        while (i < text.Length)
        {
            // Skip whitespace
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            // Read key
            var keyStart = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '=') break;

            var key = text[keyStart..i];
            i++; // skip '='

            // Read value (possibly quoted)
            string value;
            if (i < text.Length && text[i] == '\'')
            {
                i++; // skip opening quote
                var valueStart = i;
                while (i < text.Length && text[i] != '\'')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++; // skip escaped char
                    i++;
                }

                value = text[valueStart..i].Replace("\\'", "'").Replace("\\\\", "\\");
                if (i < text.Length) i++; // skip closing quote
            }
            else
            {
                var valueStart = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                value = text[valueStart..i];
            }

            result[key] = value;
        }

        return result;
    }
}
