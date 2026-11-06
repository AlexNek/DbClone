using CanonicalModel.DTOs;
using CanonicalModel.Enums;

using Npgsql;

namespace DbAdapter.PostgreSql.Providers;

public sealed class PgConnectionStringService
{
    public string BuildKeyValue(ConnectionStringFields fields)
    {
        var builder = new NpgsqlConnectionStringBuilder
                          {
                              Host = fields.Host,
                              Port = fields.Port,
                              Database = fields.Database,
                              Username = fields.Username,
                              Password = fields.Password,
                              SslMode = MapSslMode(fields.SslMode)
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
                SslMode: MapSslMode(builder.SslMode));
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

        // URI values like "verify-ca" need the hyphen removed to match the ESslMode enum
        var sslMode = parsed.QueryParams.TryGetValue("sslmode", out var rawSslMode)
            && Enum.TryParse<ESslMode>(rawSslMode.Replace("-", string.Empty), true, out var parsedSslMode)
                          ? parsedSslMode
                          : ESslMode.Prefer;

        fields = new ConnectionStringFields(
            parsed.Host,
            parsed.Port,
            parsed.Database,
            parsed.Username ?? "postgres",
            parsed.Password ?? string.Empty,
            sslMode);
        return true;
    }

    private static SslMode MapSslMode(ESslMode sslMode) =>
        sslMode switch
        {
            ESslMode.Disable => SslMode.Disable,
            ESslMode.Require => SslMode.Require,
            ESslMode.VerifyCA => SslMode.VerifyCA,
            ESslMode.VerifyFull => SslMode.VerifyFull,
            _ => SslMode.Prefer
        };

    private static ESslMode MapSslMode(SslMode sslMode) =>
        sslMode switch
        {
            SslMode.Disable => ESslMode.Disable,
            SslMode.Require => ESslMode.Require,
            SslMode.VerifyCA => ESslMode.VerifyCA,
            SslMode.VerifyFull => ESslMode.VerifyFull,
            _ => ESslMode.Prefer
        };
}
