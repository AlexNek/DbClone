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
        try
        {
            var uri = new Uri(value);
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 5432;

            var database = string.Empty;
            if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            {
                database = uri.AbsolutePath.TrimStart('/');
            }

            var username = "postgres";
            var password = string.Empty;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1)
                {
                    password = Uri.UnescapeDataString(parts[1]);
                }
            }

            fields = new ConnectionStringFields(host, port, database, username, password, "Prefer");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
