using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

using Npgsql;

namespace DbClone.PostgreSql.Formats;

/// <summary>
/// Npgsql / ADO.NET key-value format: Host=localhost;Port=5432;Database=mydb;Username=user;Password=pass
/// </summary>
public sealed class PostgreSqlNpgsqlFormat : IConnectionFormat
{
    public int DetectionPriority => 30;

    public string DisplayName => "Npgsql / .NET";

    public string Id => "pg-npgsql";

    public EDatabaseProvider Provider => EDatabaseProvider.PostgreSql;

    public string TypicalSource => ".NET / C#";

    public bool CanExport(DatabaseConnection connection) =>
        connection.Provider == EDatabaseProvider.PostgreSql;

    public bool CanImport(string text)
    {
        // Must NOT be a URI or JDBC string
        if (text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase))
            return false;

        // ADO.NET key-value strings are semicolon-separated (libpq uses spaces).
        if (!text.Contains(';'))
            return false;

        // Accept if any common Npgsql/ADO.NET key is present
        return text.Contains("Host=", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Server=", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Database=", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Username=", StringComparison.OrdinalIgnoreCase)
               || text.Contains("User Id=", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Port=", StringComparison.OrdinalIgnoreCase);
    }

    public string Export(DatabaseConnection connection)
    {
        var builder = new NpgsqlConnectionStringBuilder
                          {
                              Host = connection.Host,
                              Port = connection.Port,
                              Database = connection.Database,
                              Username = connection.Username,
                              Password = connection.Password ?? string.Empty,
                              SslMode = MapSslMode(connection.SslMode)
                          };

        return builder.ConnectionString;
    }

    public DatabaseConnection Parse(string text)
    {
        // Parse tolerantly: split key-value pairs manually, skip unrecognized keys
        // instead of letting NpgsqlConnectionStringBuilder throw on the first bad key.
        var builder = new NpgsqlConnectionStringBuilder();
        var invalidKeys = new List<string>();

        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = pair[..eqIndex].Trim();
            var value = pair[(eqIndex + 1)..].Trim();

            try
            {
                builder[key] = value;
            }
            catch (ArgumentException)
            {
                // Unrecognized key — skip it and remember for warnings
                invalidKeys.Add(key);
            }
        }

        // Only preserve options that were explicitly present in the input text,
        // not defaults added by NpgsqlConnectionStringBuilder.
        var inputKeys = ParseInputKeys(text);

        // Use actual parsed values for keys that succeeded, -1/empty for keys that were
        // not present (either missing or rejected as invalid).
        var hasHost = inputKeys.Contains("Host") || inputKeys.Contains("Server");
        var hasPort = inputKeys.Contains("Port");
        var hasUsername = inputKeys.Contains("Username") || inputKeys.Contains("User Id");

        var connection = new DatabaseConnection
                             {
                                 Provider = EDatabaseProvider.PostgreSql,
                                 Host = hasHost ? builder.Host ?? "localhost" : string.Empty,
                                 Port = hasPort ? builder.Port : -1,
                                 Database = builder.Database ?? string.Empty,
                                 Username =
                                     hasUsername ? builder.Username ?? string.Empty : string.Empty,
                                 Password =
                                     string.IsNullOrEmpty(builder.Password)
                                         ? null
                                         : builder.Password,
                                 SslMode = MapSslMode(builder.SslMode)
                             };

        if (inputKeys.Contains("Application Name") &&
            builder.TryGetValue("Application Name", out var appName) && appName is string an
            && !string.IsNullOrEmpty(an))
            connection.Options["Application Name"] = an;
        if (inputKeys.Contains("Timeout") &&
            builder.TryGetValue("Timeout", out var timeout) && timeout is not null)
            connection.Options["Timeout"] = timeout.ToString()!;
        if (inputKeys.Contains("Search Path") &&
            builder.TryGetValue("Search Path", out var searchPath) && searchPath is string sp
            && !string.IsNullOrEmpty(sp))
            connection.Options["Search Path"] = sp;
        if (inputKeys.Contains("Pooling") &&
            builder.TryGetValue("Pooling", out var pooling) && pooling is not null)
            connection.Options["Pooling"] = pooling.ToString()!;

        // Store invalid keys so the import service can produce warnings
        foreach (var key in invalidKeys)
            connection.Options[$"_invalid:{key}"] = string.Empty;

        return connection;
    }

    private static ESslMode MapSslMode(SslMode mode) =>
        mode switch
            {
                SslMode.Disable => ESslMode.Disable,
                SslMode.Prefer => ESslMode.Prefer,
                SslMode.Require => ESslMode.Require,
                SslMode.VerifyCA => ESslMode.VerifyCA,
                SslMode.VerifyFull => ESslMode.VerifyFull,
                _ => ESslMode.Prefer
            };

    private static SslMode MapSslMode(ESslMode mode) =>
        mode switch
            {
                ESslMode.Disable => SslMode.Disable,
                ESslMode.Prefer => SslMode.Prefer,
                ESslMode.Require => SslMode.Require,
                ESslMode.VerifyCA => SslMode.VerifyCA,
                ESslMode.VerifyFull => SslMode.VerifyFull,
                _ => SslMode.Prefer
            };

    /// <summary>
    /// Extracts the key names explicitly present in the raw input text.
    /// </summary>
    private static HashSet<string> ParseInputKeys(string text)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex > 0)
                keys.Add(pair[..eqIndex].Trim());
        }

        return keys;
    }
}
