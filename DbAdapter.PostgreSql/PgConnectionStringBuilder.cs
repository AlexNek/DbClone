using DbAdapter.Definitions;

using Npgsql;

namespace DbAdapter.PostgreSql;

/// <summary>
/// Shared connection string builder for all PostgreSQL providers.
/// </summary>
internal static class PgConnectionStringBuilder
{
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
}
