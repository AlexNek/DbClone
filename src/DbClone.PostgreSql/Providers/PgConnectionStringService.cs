using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;

using Npgsql;

namespace DbClone.PostgreSql.Providers;

public sealed class PgConnectionStringService : IConnectionStringService
{
    public string BuildKeyValue(ConnectionStringFields fields)
    {
        var sslMode = Enum.TryParse<SslMode>(fields.SslMode, true, out var ssl)
                          ? ssl
                          : SslMode.Prefer;

        var builder = new NpgsqlConnectionStringBuilder
                          {
                              Host = fields.Host,
                              Port = fields.Port,
                              Database = fields.Database,
                              Username = fields.Username,
                              Password = fields.Password,
                              SslMode = sslMode
                          };
        return builder.ConnectionString;
    }

    public bool TryParse(string value, out ConnectionStringFields fields)
    {
        if (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseUri(value, out fields);
        }

        return TryParseKeyValue(value, out fields);
    }

    public bool TryParseKeyValue(string value, out ConnectionStringFields fields)
    {
        fields = default!;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(value);
            fields = new ConnectionStringFields(
                Host: builder.Host ?? "localhost",
                Port: builder.Port,
                Database: builder.Database ?? "",
                Username: builder.Username ?? "postgres",
                Password: builder.Password ?? "",
                SslMode: builder.SslMode.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseUri(string value, out ConnectionStringFields fields)
    {
        fields = default!;

        var parsed = Formats.PostgresUriParser.TryParse(value);
        if (parsed is null)
            return false;

        // URI values like "verify-ca" need the hyphen removed to match the SslMode enum
        var sslMode = parsed.QueryParams.TryGetValue("sslmode", out var rawSslMode)
            && Enum.TryParse<SslMode>(rawSslMode.Replace("-", string.Empty), true, out var parsedSslMode)
                          ? parsedSslMode
                          : SslMode.Prefer;

        fields = new ConnectionStringFields(
            parsed.Host,
            parsed.Port,
            parsed.Database,
            parsed.Username ?? "postgres",
            parsed.Password ?? string.Empty,
            sslMode.ToString());
        return true;
    }
}
