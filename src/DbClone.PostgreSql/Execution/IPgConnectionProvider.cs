using DbClone.Application.Copy;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Execution;

/// <summary>
/// Provides validated database connections for pipeline stages.
/// Abstraction over <see cref="PgConnectionHelper"/> so stages can be unit-tested
/// without a live database.
/// </summary>
public interface IPgConnectionProvider
{
    /// <summary>
    /// Returns a validated destination connection, or <c>null</c> if the connection
    /// cannot be re-established.
    /// </summary>
    Task<NpgsqlConnection?> GetDestinationConnectionAsync(
        CopyContext context,
        ILogger logger,
        CancellationToken cancellationToken);
}
