using System.Data;


using Microsoft.Extensions.Logging;

using Npgsql;
using CanonicalModel.Enums;
using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Execution;

/// <summary>
/// Helper methods for managing Npgsql connections across pipeline stages.
/// </summary>
public static class PgConnectionHelper
{
    /// <summary>
    /// Ensures the destination connection is open. If it was closed or broken, reopens it
    /// using the stored connection string from <see cref="CopyContext"/>.
    /// </summary>
    public static async Task<NpgsqlConnection> EnsureDestinationOpenAsync(
        CopyContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var handle = context.DestinationConnection as PgCopyConnection
                     ?? throw new InvalidOperationException("Destination connection is not a PostgreSQL copy handle.");
        return await EnsureOpenAsync(handle, ECompareSide.Destination, logger, cancellationToken);
    }

    /// <summary>
    /// Ensures the source connection is open. If it was closed or broken, reopens it
    /// using the stored connection string from <see cref="CopyContext"/>.
    /// </summary>
    public static async Task<NpgsqlConnection> EnsureSourceOpenAsync(
        CopyContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var handle = context.SourceConnection as PgCopyConnection
                     ?? throw new InvalidOperationException("Source connection is not a PostgreSQL copy handle.");
        return await EnsureOpenAsync(handle, ECompareSide.Source, logger, cancellationToken);
    }

    /// <summary>
    /// Validates a connection with SELECT 1. If it fails (e.g. disposed), forces a reopen.
    /// Returns null if the connection cannot be re-established.
    /// </summary>
    public static async Task<NpgsqlConnection?> ValidateAndReopenAsync(
        CopyContext context,
        bool isSource,
        ILogger logger,
        CancellationToken ct)
    {
        var side = isSource ? ECompareSide.Source : ECompareSide.Destination;

        try
        {
            var conn = isSource
                           ? await EnsureSourceOpenAsync(context, logger, ct)
                           : await EnsureDestinationOpenAsync(context, logger, ct);

            // Probe with a real query to detect dead/disposed connections
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.CommandTimeout = 10;
            await cmd.ExecuteScalarAsync(ct);

            return conn;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Side} connection validation failed, forcing reopen", side);

            try
            {
                var handle = (isSource ? context.SourceConnection : context.DestinationConnection) as PgCopyConnection
                             ?? throw new InvalidOperationException($"{side} connection handle is missing.");
                await handle.ReopenAsync(ct);
                return handle.Current;
            }
            catch (Exception reopenEx)
            {
                logger.LogError(reopenEx, "Failed to reopen {Side} connection", side);
                return null;
            }
        }
    }

    private static async Task<NpgsqlConnection> EnsureOpenAsync(
        PgCopyConnection handle,
        ECompareSide side,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (handle.Current.State is ConnectionState.Open)
                return handle.Current;
        }
        catch (ObjectDisposedException)
        {
            logger.LogWarning("{Side} connection was disposed, reopening", side);
        }

        logger.LogInformation("Reopening {Side} connection", side);
        await handle.ReopenAsync(cancellationToken);
        logger.LogInformation("{Side} connection reopened successfully", side);
        return handle.Current;
    }

}
