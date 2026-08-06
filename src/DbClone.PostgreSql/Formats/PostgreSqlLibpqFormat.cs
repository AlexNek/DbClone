using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// libpq / psql keyword=value format: host=localhost port=5432 dbname=mydb user=postgres password=secret
/// </summary>
public sealed class PostgreSqlLibpqFormat : IConnectionFormat
{
    public int DetectionPriority => 40;

    public string DisplayName => "libpq / psql";

    public string Id => "pg-libpq";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => "C / psql / pgAdmin";

    public bool CanExport(DatabaseConnection connection) =>
        connection.Provider == EDatabaseProvider.PostgreSql;

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

    public string Export(DatabaseConnection connection)
    {
        var parts = new List<string>
                        {
                            $"host={connection.Host}",
                            $"port={connection.Port}",
                            $"dbname={connection.Database}",
                            $"user={connection.Username}"
                        };

        if (!string.IsNullOrEmpty(connection.Password))
            parts.Add($"password={connection.Password}");

        if (connection.SslMode != ESslMode.Prefer)
            parts.Add($"sslmode={PostgreSqlUriFormat.FormatSslMode(connection.SslMode)}");

        foreach (var opt in connection.Options)
            parts.Add($"{opt.Key.ToLowerInvariant()}={opt.Value}");

        return string.Join(" ", parts);
    }

    public DatabaseConnection Parse(string text)
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

        foreach (var pair in pairs)
        {
            if (!knownKeys.Contains(pair.Key))
                connection.Options[pair.Key] = pair.Value;
        }

        return connection;
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
