using DbAdapter.Definitions;
using DbClone.Application.DTOs;

using Npgsql;
using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql;

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

    /// <summary>
    /// Reads a target's connection text back into a builder.
    /// </summary>
    /// <remarks>
    /// The text the application writes carries host, port, database, user, password and TLS
    /// only, and a re-parse drops the two settings the block relies on, so the connect timeout
    /// is applied here on every string.
    /// </remarks>
    public static NpgsqlConnectionStringBuilder ParseConnectionString(DatabaseTarget target) =>
        new(target.ConnectionString) { Timeout = 30 };

    /// <summary>
    /// Builds a copy connection string from a target, re-applying the keep-alive the
    /// connection text does not carry.
    /// </summary>
    public static string BuildCopyConnectionString(DatabaseTarget target)
    {
        var builder = ParseConnectionString(target);
        builder.KeepAlive = 30;
        return builder.ConnectionString;
    }

    /// <summary>
    /// The host, port and database a target describes, read out of its connection text —
    /// the endpoint the block logs.
    /// </summary>
    public static string Describe(DatabaseTarget target)
    {
        var builder = new NpgsqlConnectionStringBuilder(target.ConnectionString);
        return $"{builder.Host}:{builder.Port}/{builder.Database}";
    }

    /// <summary>
    /// The host a target connects to, read out of its connection text.
    /// </summary>
    public static string HostOf(DatabaseTarget target) =>
        new NpgsqlConnectionStringBuilder(target.ConnectionString).Host ?? string.Empty;

    /// <summary>
    /// The profile a target's connection text describes, for the classes that still take it.
    /// </summary>
    public static ConnectionInfo ToConnectionInfo(DatabaseTarget target)
    {
        var builder = new NpgsqlConnectionStringBuilder(target.ConnectionString);
        return new ConnectionInfo(
            builder.Host ?? "localhost",
            builder.Port,
            builder.Database ?? string.Empty,
            builder.Username ?? "postgres",
            builder.Password ?? string.Empty,
            MapSslMode(builder.SslMode));
    }

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
