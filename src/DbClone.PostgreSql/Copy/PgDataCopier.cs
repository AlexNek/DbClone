using System.Data;
using System.Diagnostics;

using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Copy;

/// <summary>
/// PostgreSQL implementation of <see cref="IDataCopier"/>.
/// Uses binary COPY for maximum throughput with INSERT fallback.
/// Automatically reopens dropped connections and retries failed tables.
/// </summary>
public sealed class PgDataCopier : IDataCopier
{
    private const int MaxRetriesPerTable = 2;

    private readonly string? _destinationConnectionString;

    private readonly List<string> _failedTables = [];

    private readonly ILogger<PgDataCopier> _logger;

    private readonly string? _sourceConnectionString;

    private NpgsqlConnection _destinationConnection;

    private NpgsqlConnection _sourceConnection;

    /// <summary>
    /// Gets the list of tables that failed to copy during the last <see cref="CopyDataAsync"/> call.
    /// Each entry contains the table name and error message.
    /// </summary>
    public IReadOnlyList<string> FailedTables => _failedTables;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgDataCopier"/> class.
    /// </summary>
    /// <param name="sourceConnection">Open source connection.</param>
    /// <param name="destinationConnection">Open destination connection.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="sourceConnectionString">
    /// Optional connection string for reopening the source if it drops.
    /// </param>
    /// <param name="destinationConnectionString">
    /// Optional connection string for reopening the destination if it drops.
    /// </param>
    public PgDataCopier(
        NpgsqlConnection sourceConnection,
        NpgsqlConnection destinationConnection,
        ILogger<PgDataCopier> logger,
        string? sourceConnectionString = null,
        string? destinationConnectionString = null)
    {
        _sourceConnection =
            sourceConnection ?? throw new ArgumentNullException(nameof(sourceConnection));
        _destinationConnection = destinationConnection
                                 ?? throw new ArgumentNullException(nameof(destinationConnection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sourceConnectionString = sourceConnectionString;
        _destinationConnectionString = destinationConnectionString;
    }

    /// <inheritdoc />
    public async Task<CopyStatistics> CopyDataAsync(
        IReadOnlyList<TableDefinition> tables,
        CopyOptions options,
        IProgress<TableCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long totalRowsCopied = 0;
        long totalBytesTransferred = 0;
        int tablesCopied = 0;
        _failedTables.Clear();

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tableKey = $"{table.SchemaName}.{table.Name}";
            _logger.LogInformation("Copying data for table {Table}", tableKey);

            var sw = Stopwatch.StartNew();
            var success = false;

            for (var attempt = 0; attempt <= MaxRetriesPerTable; attempt++)
            {
                try
                {
                    // Ensure both connections are alive before each table
                    await EnsureConnectionsOpenAsync(cancellationToken);

                    var (rowsCopied, bytesTransferred) =
                        await CopyTableAsync(table, options, progress, cancellationToken);

                    sw.Stop();

                    totalRowsCopied += rowsCopied;
                    totalBytesTransferred += bytesTransferred;
                    tablesCopied++;

                    if (rowsCopied > 0)
                    {
                        _logger.LogInformation(
                            "Table {Table}: {Rows} rows ({Bytes:N0} bytes) in {Elapsed}",
                            tableKey,
                            rowsCopied,
                            bytesTransferred,
                            sw.Elapsed);
                    }
                    else
                    {
                        _logger.LogDebug("Table {Table} is empty (0 rows)", tableKey);
                    }

                    success = true;
                    break;
                }
                catch (Exception ex) when (attempt < MaxRetriesPerTable && IsConnectionError(ex))
                {
                    _logger.LogWarning(
                        ex,
                        "Table {Table}: connection error on attempt {Attempt}/{MaxRetries}, reopening connections",
                        tableKey,
                        attempt + 1,
                        MaxRetriesPerTable + 1);

                    await ReopenBrokenConnectionsAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(
                        ex,
                        "Table {Table}: copy failed after {Attempts} attempt(s)",
                        tableKey,
                        attempt + 1);
                    _failedTables.Add($"{tableKey}: {PgExceptionHelper.GetUserMessage(ex)}");
                    break;
                }
            }

            if (!success && !_failedTables.Any(f => f.StartsWith(tableKey)))
            {
                _failedTables.Add($"{tableKey}: max retries exceeded");
            }
        }

        if (_failedTables.Count > 0)
        {
            _logger.LogWarning(
                "Data copy completed with {Failed} failed table(s): {Tables}",
                _failedTables.Count,
                string.Join(", ", _failedTables));
        }

        return new CopyStatistics(
            TotalRowsCopied: totalRowsCopied,
            TotalBytesTransferred: totalBytesTransferred,
            TablesCopied: tablesCopied,
            ViewsCopied: 0,
            FunctionsCopied: 0,
            TriggersCopied: 0,
            IndexesCreated: 0,
            ConstraintsCreated: 0,
            SequencesSynced: 0,
            TablesFailed: _failedTables.Count);
    }

    /// <summary>
    /// Builds the row-count query for a source table.
    /// ONLY: reads just this table's own rows. For a legacy-inheritance parent a plain
    /// SELECT would also return its children's rows, which are copied separately when
    /// their own tables are processed — double-copying them into this table.
    /// </summary>
    internal static string BuildRowCountSql(string qualifiedName) =>
        $"SELECT count(*) FROM ONLY {qualifiedName}";

    /// <summary>
    /// Builds the source SELECT used by both the binary-COPY and INSERT fallback paths.
    /// Uses ONLY for the same reason as <see cref="BuildRowCountSql"/>.
    /// </summary>
    internal static string BuildSelectSql(string columnList, string qualifiedName) =>
        $"SELECT {columnList} FROM ONLY {qualifiedName}";

    private async Task<(long RowsCopied, long BytesTransferred)> CopyTableAsync(
        TableDefinition table,
        CopyOptions options,
        IProgress<TableCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var qualifiedName = PgIdentifierQuoter.QuoteSchemaQualified(table.SchemaName, table.Name);

        // Partitioned parents hold no data themselves — their leaf partitions are copied
        // individually, so skip them to avoid double-copying through attached partitions.
        if (table.IsPartitioned)
        {
            _logger.LogDebug(
                "Skipping partitioned table {Table}: data is copied via its partitions",
                qualifiedName);
            return (0, 0);
        }

        // Generated columns cannot be written to — exclude them from the copy column list
        var copyColumns = table.Columns.Where(c => !c.IsGenerated).ToList();
        var columnNames = copyColumns.Select(c => PgIdentifierQuoter.QuoteIdentifier(c.Name));
        var columnList = string.Join(", ", columnNames);

        // Get row count from source (ONLY — see BuildRowCountSql).
        long totalRows;
        await using var countCmd = new NpgsqlCommand(
            BuildRowCountSql(qualifiedName),
            _sourceConnection);
        countCmd.CommandTimeout = 60;
        totalRows = (long)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        if (totalRows == 0)
            return (0, 0);

        // Tables with jsonb columns cannot use binary COPY across different PG major versions
        // (jsonb binary format changed in PG 17). Use INSERT fallback directly.
        if (HasJsonbColumns(table))
        {
            _logger.LogInformation(
                "Table {Table} has jsonb columns, using INSERT instead of binary COPY",
                qualifiedName);
            return await CopyWithInsertAsync(
                       table,
                       qualifiedName,
                       copyColumns,
                       columnList,
                       options,
                       totalRows,
                       progress,
                       cancellationToken);
        }

        // Build COPY FROM STDIN command
        var copyCommand = $"COPY {qualifiedName} ({columnList}) FROM STDIN (FORMAT BINARY)";

        try
        {
            return await CopyWithBinaryImportAsync(
                       table,
                       qualifiedName,
                       columnList,
                       copyCommand,
                       options,
                       totalRows,
                       progress,
                       cancellationToken);
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Connection error during binary import — force-close the destination to clear
            // any broken COPY state, then re-throw so the retry loop reopens connections.
            // Do NOT try to reopen here — let ReopenBrokenConnectionsAsync handle it
            // to avoid leaving _destinationConnection in a half-open state.
            _logger.LogWarning(
                ex,
                "Connection error during binary import for {Table}, forcing destination cleanup",
                qualifiedName);
            try
            {
                await _destinationConnection.CloseAsync();
            }
            catch
            {
            }

            try
            {
                await _destinationConnection.DisposeAsync();
            }
            catch
            {
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Binary import failed for {Table}, falling back to INSERT",
                qualifiedName);
            return await CopyWithInsertAsync(
                       table,
                       qualifiedName,
                       copyColumns,
                       columnList,
                       options,
                       totalRows,
                       progress,
                       cancellationToken);
        }
    }

    private async Task<(long RowsCopied, long BytesTransferred)> CopyWithBinaryImportAsync(
        TableDefinition table,
        string qualifiedName,
        string columnList,
        string copyCommand,
        CopyOptions options,
        long totalRows,
        IProgress<TableCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selectSql = BuildSelectSql(columnList, qualifiedName);
        long rowsCopied = 0;
        long bytesTransferred = 0;
        var sw = Stopwatch.StartNew();

        await using var sourceCmd = new NpgsqlCommand(selectSql, _sourceConnection);
        sourceCmd.CommandTimeout = 300;
        await using var reader = await sourceCmd.ExecuteReaderAsync(cancellationToken);

        await using var writer =
            await _destinationConnection.BeginBinaryImportAsync(copyCommand, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await writer.StartRowAsync(cancellationToken);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (await reader.IsDBNullAsync(i, cancellationToken))
                {
                    await writer.WriteNullAsync(cancellationToken);
                }
                else
                {
                    await writer.WriteAsync(reader.GetValue(i), cancellationToken);
                }
            }

            rowsCopied++;
            bytesTransferred += 100; // Rough estimate since binary size isn't directly available

            // Report progress periodically
            if (rowsCopied % options.BatchSize == 0)
            {
                progress?.Report(
                    new TableCopyProgress(
                        table.SchemaName,
                        table.Name,
                        rowsCopied,
                        totalRows,
                        sw.Elapsed));
            }
        }

        await writer.CompleteAsync(cancellationToken);
        sw.Stop();

        progress?.Report(
            new TableCopyProgress(
                table.SchemaName,
                table.Name,
                rowsCopied,
                totalRows,
                sw.Elapsed));

        return (rowsCopied, bytesTransferred);
    }

    /// <summary>
    /// INSERT-based fallback. Every column is read as text via PostgreSQL's own output
    /// functions (::text) and re-inserted with an explicit cast back to its data type.
    /// This round-trips ANY value losslessly — arrays, ranges, jsonb, inet, interval,
    /// numerics beyond decimal range, infinity timestamps — and always produces valid SQL,
    /// unlike formatting CLR objects by hand.
    /// </summary>
    private async Task<(long RowsCopied, long BytesTransferred)> CopyWithInsertAsync(
        TableDefinition table,
        string qualifiedName,
        IReadOnlyList<ColumnDefinition> copyColumns,
        string columnList,
        CopyOptions options,
        long totalRows,
        IProgress<TableCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selectColumns =
            copyColumns.Select(c => $"{PgIdentifierQuoter.QuoteIdentifier(c.Name)}::text");
        var selectSql = BuildSelectSql(string.Join(", ", selectColumns), qualifiedName);

        long rowsCopied = 0;
        long bytesTransferred = 0;
        var sw = Stopwatch.StartNew();

        await using var sourceCmd = new NpgsqlCommand(selectSql, _sourceConnection);
        sourceCmd.CommandTimeout = 300;
        await using var reader = await sourceCmd.ExecuteReaderAsync(cancellationToken);

        var batch = new List<string?[]>();

        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, cancellationToken)
                             ? null
                             : reader.GetString(i);
            }

            batch.Add(row);
            rowsCopied++;
            bytesTransferred += 100;

            if (batch.Count >= options.BatchSize)
            {
                await InsertBatchAsync(
                    qualifiedName,
                    copyColumns,
                    columnList,
                    batch,
                    cancellationToken);
                batch.Clear();

                progress?.Report(
                    new TableCopyProgress(
                        table.SchemaName,
                        table.Name,
                        rowsCopied,
                        totalRows,
                        sw.Elapsed));
            }
        }

        // Insert remaining rows
        if (batch.Count > 0)
        {
            await InsertBatchAsync(
                qualifiedName,
                copyColumns,
                columnList,
                batch,
                cancellationToken);
        }

        sw.Stop();
        progress?.Report(
            new TableCopyProgress(
                table.SchemaName,
                table.Name,
                rowsCopied,
                totalRows,
                sw.Elapsed));

        return (rowsCopied, bytesTransferred);
    }

    private async Task<NpgsqlConnection> EnsureConnectionOpenAsync(
        NpgsqlConnection connection,
        string? connectionString,
        ECompareSide side,
        CancellationToken ct)
    {
        // Already open — verify with a probe
        try
        {
            if (connection is { State: ConnectionState.Open })
            {
                try
                {
                    await using var probe = new NpgsqlCommand("SELECT 1", connection);
                    probe.CommandTimeout = 10;
                    await probe.ExecuteScalarAsync(ct);
                    return connection; // Connection is alive
                }
                catch
                {
                    _logger.LogWarning("{Side} connection probe failed, reopening", side);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            _logger.LogWarning("{Side} connection was disposed, reopening", side);
        }

        // Connection is closed/broken/disposed — reopen
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException(
                $"Cannot reopen {side} connection: no connection string available");

        _logger.LogInformation("Reopening {Side} connection", side);

        try
        {
            await connection.CloseAsync();
        }
        catch
        {
            /* best effort */
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch
        {
            /* best effort */
        }

        var newConn = new NpgsqlConnection(connectionString);
        await newConn.OpenAsync(ct);

        _logger.LogInformation("{Side} connection reopened successfully", side);
        return newConn;
    }

    /// <summary>
    /// Checks whether both connections are open and responsive.
    /// Sends a lightweight SELECT 1 probe on each.
    /// </summary>
    private async Task EnsureConnectionsOpenAsync(CancellationToken ct)
    {
        _sourceConnection = await EnsureConnectionOpenAsync(
                                _sourceConnection,
                                _sourceConnectionString,
                                ECompareSide.Source,
                                ct);
        _destinationConnection = await EnsureConnectionOpenAsync(
                                     _destinationConnection,
                                     _destinationConnectionString,
                                     ECompareSide.Destination,
                                     ct);
    }

    private static async Task<NpgsqlConnection> ForceReopenAsync(
        NpgsqlConnection connection,
        string? connectionString,
        ECompareSide side,
        CancellationToken ct)
    {
        // Always dispose old connection to clear any broken COPY/transaction state
        try
        {
            await connection.CloseAsync();
        }
        catch
        {
            /* best effort */
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch
        {
            /* best effort */
        }

        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException(
                $"Cannot reopen {side} connection: no connection string available");

        var newConn = new NpgsqlConnection(connectionString);
        await newConn.OpenAsync(ct);
        return newConn;
    }

    /// <summary>
    /// Checks whether a table has any jsonb columns (including jsonb arrays).
    /// jsonb binary format is not compatible across PG major versions (changed in PG 17),
    /// so tables with jsonb must use INSERT instead of binary COPY.
    /// </summary>
    private static bool HasJsonbColumns(TableDefinition table)
    {
        return table.Columns.Any(c =>
            c.DataType.StartsWith("jsonb", StringComparison.OrdinalIgnoreCase));
    }

    private async Task InsertBatchAsync(
        string qualifiedName,
        IReadOnlyList<ColumnDefinition> copyColumns,
        string columnList,
        List<string?[]> batch,
        CancellationToken cancellationToken)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"INSERT INTO {qualifiedName} ({columnList}) VALUES ");

        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(", ");

            sb.Append('(');
            for (var j = 0; j < batch[i].Length; j++)
            {
                if (j > 0) sb.Append(", ");

                var value = batch[i][j];
                sb.Append(
                    value is null
                        ? "NULL"
                        : $"'{value.Replace("'", "''")}'::{copyColumns[j].DataType}");
            }

            sb.Append(')');
        }

        sb.Append(';');

        await using var insertCmd = new NpgsqlCommand(sb.ToString(), _destinationConnection);
        insertCmd.CommandTimeout = 300;
        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Determines whether an exception is caused by a dropped/closed connection
    /// and is therefore retryable.
    /// </summary>
    private static bool IsConnectionError(Exception ex)
    {
        if (ex is NpgsqlException { IsTransient: true })
            return true;

        if (ex is ObjectDisposedException)
            return true;

        var msg = ex.Message;
        return msg.Contains("not open", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("connection is closed", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("broken", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("NpgsqlBroken", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("disposed", StringComparison.OrdinalIgnoreCase)
               || ex is IOException
               || ex.InnerException is IOException
               || ex.InnerException is ObjectDisposedException;
    }

    /// <summary>
    /// Forcefully reopens both connections after a failure.
    /// Always creates brand-new connections to avoid stale COPY state.
    /// </summary>
    private async Task ReopenBrokenConnectionsAsync(CancellationToken ct)
    {
        _sourceConnection = await ForceReopenAsync(
                                _sourceConnection,
                                _sourceConnectionString,
                                ECompareSide.Source,
                                ct);
        _destinationConnection = await ForceReopenAsync(
                                     _destinationConnection,
                                     _destinationConnectionString,
                                     ECompareSide.Destination,
                                     ct);
    }
}
