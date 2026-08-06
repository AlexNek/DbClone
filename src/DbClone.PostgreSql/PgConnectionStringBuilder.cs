using DbClone.Application.DTOs;
using DbClone.Application.Enums;

using Npgsql;

namespace DbClone.PostgreSql;

/// <summary>
/// Shared connection string builder for all PostgreSQL providers.
/// </summary>
internal static class PgConnectionStringBuilder
{
    public static NpgsqlConnectionStringBuilder BuildConnectionString(
        ConnectionInfo connection,
        string? databaseOverride = null)
    {
        return new NpgsqlConnectionStringBuilder
                   {
                       Host = connection.Host,
                       Port = connection.Port,
                       Database = databaseOverride ?? connection.DatabaseName,
                       Username = connection.Username,
                       Password = connection.Password,
                       SslMode = MapSslMode(connection.SslMode),
                       Timeout = 30
                   };
    }

    /// <summary>
    /// Builds a connection string with additional settings for long-running copy operations.
    /// </summary>
    public static string BuildCopyConnectionString(ConnectionInfo connection)
    {
        var builder = BuildConnectionString(connection);
        builder.KeepAlive = 30;
        return builder.ConnectionString;
    }

    public static SslMode MapSslMode(ESslMode sslMode) =>
        sslMode switch
            {
                ESslMode.Disable => SslMode.Disable,
                ESslMode.Require => SslMode.Require,
                _ => SslMode.Prefer
            };
}
