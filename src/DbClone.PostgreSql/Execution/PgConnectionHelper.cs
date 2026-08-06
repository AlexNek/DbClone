using System.Data;

using DbClone.Application.Copy;
using DbClone.Application.Enums;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Execution;

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
        var conn = await EnsureOpenAsync(
                       context.DestinationConnection as NpgsqlConnection,
                       context.DestinationConnectionString,
                       ECompareSide.Destination,
                       logger,
                       cancellationToken);

        // Update context if we created a new connection
        if (!ReferenceEquals(conn, context.DestinationConnection))
            context.DestinationConnection = conn;

        return conn;
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
        var conn = await EnsureOpenAsync(
                       context.SourceConnection as NpgsqlConnection,
                       context.SourceConnectionString,
                       ECompareSide.Source,
                       logger,
                       cancellationToken);

        // Update context if we created a new connection
        if (!ReferenceEquals(conn, context.SourceConnection))
            context.SourceConnection = conn;

        return conn;
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
                var oldConn = isSource
                                  ? context.SourceConnection as NpgsqlConnection
                                  : context.DestinationConnection as NpgsqlConnection;
                if (oldConn is not null)
                {
                    try
                    {
                        await oldConn.CloseAsync();
                    }
                    catch
                    {
                    }

                    try
                    {
                        await oldConn.DisposeAsync();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            if (isSource) context.SourceConnection = null;
            else context.DestinationConnection = null;

            try
            {
                return isSource
                           ? await EnsureSourceOpenAsync(context, logger, ct)
                           : await EnsureDestinationOpenAsync(context, logger, ct);
            }
            catch (Exception reopenEx)
            {
                logger.LogError(reopenEx, "Failed to reopen {Side} connection", side);
                return null;
            }
        }
    }

    private static async Task<NpgsqlConnection> EnsureOpenAsync(
        NpgsqlConnection? connection,
        string? connectionString,
        ECompareSide side,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Connection is already open and usable (guard against disposed)
        try
        {
            if (connection is { State: ConnectionState.Open })
                return connection;
        }
        catch (ObjectDisposedException)
        {
            // Connection was disposed (e.g. by PgDataCopier internal reopen) — treat as dead
            logger.LogWarning("{Side} connection was disposed, reopening", side);
        }

        // Try to close existing connection if it's not already closed
        if (connection is not null)
        {
            try
            {
                try
                {
                    if (connection.State is not ConnectionState.Closed)
                    {
                        logger.LogWarning(
                            "{Side} connection is in {State} state, attempting to reopen",
                            side,
                            connection.State);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, skip close
                }

                await connection.CloseAsync();
            }
            catch
            {
                // Ignore errors on broken/disposed connections
            }
        }

        // Reopen using stored connection string
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException(
                $"Cannot reopen {side} connection: no connection string available");

        logger.LogInformation("Reopening {Side} connection", side);

        var newConn = new NpgsqlConnection(connectionString);
        await newConn.OpenAsync(cancellationToken);

        logger.LogInformation("{Side} connection reopened successfully", side);
        return newConn;
    }
}
