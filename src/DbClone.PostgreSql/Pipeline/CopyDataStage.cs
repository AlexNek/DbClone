using System.Diagnostics;

using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Copies table data using binary COPY or INSERT fallback.
/// </summary>
public sealed class CopyDataStage : ICopyStage
{
    private readonly IPgExecutorFactory _executorFactory;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CopyData;

    /// <inheritdoc />
    public int Order => 90;

    /// <summary>Initializes a new instance.</summary>
    public CopyDataStage(IPgExecutorFactory executorFactory, ILoggerFactory loggerFactory)
    {
        _executorFactory = executorFactory;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Request.Options.CopyData)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopyData=false")]);

        var logger = _loggerFactory.CreateLogger<CopyDataStage>();

        // Validate connections with a real query — State=Open can be stale after idle time
        var sourceConn = await ValidateAndReopenAsync(
                             context,
                             isSource: true,
                             logger,
                             cancellationToken);
        var destConn = await ValidateAndReopenAsync(
                           context,
                           isSource: false,
                           logger,
                           cancellationToken);

        if (sourceConn is null || destConn is null)
        {
            return new StageResult(
                Name,
                false,
                TimeSpan.Zero,
                0,
                    [StageDetail.ConnectionFailed(sourceConn is null ? ECompareSide.Source : ECompareSide.Destination)]);
        }

        try
        {
            var tablesToCopy = context.SourceModel!.Tables;

            // Exclude tables that could not be created on the destination (e.g. they
            // depend on an unavailable extension). The user is notified via warnings.
            if (context.SkippedTables.Count > 0)
            {
                var excluded = tablesToCopy
                    .Where(t => context.SkippedTables.Contains(
                                    new Application.Models.TableId(t.SchemaName, t.Name)))
                    .ToList();

                if (excluded.Count > 0)
                {
                    logger.LogWarning(
                        "CopyData: skipping {Count} table(s) that could not be created on the destination",
                        excluded.Count);

                    foreach (var t in excluded)
                    {
                        logger.LogWarning("  Skipping data copy for {Table} (table creation failed earlier)",
                            $"{t.SchemaName}.{t.Name}");
                    }
                }

                tablesToCopy = tablesToCopy
                    .Where(t => !context.SkippedTables.Contains(
                                    new Application.Models.TableId(t.SchemaName, t.Name)))
                    .ToList();
            }

            // Resume/Update mode: compare row counts and only copy tables that are missing or mismatched
            if (context.Request.Options.CopyMode is ECopyMode.Resume or ECopyMode.Update)
            {
                var sourceExec = _executorFactory.Create(sourceConn);
                var destExec = _executorFactory.Create(destConn);
                var filteredTables = new List<TableDefinition>();

                foreach (var table in tablesToCopy)
                {
                    var qualifiedName =
                        PgIdentifierQuoter.QuoteSchemaQualified(table.SchemaName, table.Name);
                    try
                    {
                        var sourceRows = await sourceExec.ExecuteScalarAsync<long>(
                                             $"SELECT count(*) FROM {qualifiedName}",
                                             cancellationToken);
                        var destRows = await destExec.ExecuteScalarAsync<long>(
                                           $"SELECT count(*) FROM {qualifiedName}",
                                           cancellationToken);

                        if (sourceRows != destRows)
                        {
                            logger.LogInformation(
                                "CopyMode={Mode}: {Table} mismatch (source={Source}, dest={Dest}), will re-copy",
                                context.Request.Options.CopyMode,
                                qualifiedName,
                                sourceRows,
                                destRows);
                            filteredTables.Add(table);
                        }
                        else
                        {
                            logger.LogDebug(
                                "CopyMode={Mode}: {Table} matches ({Rows} rows), skipping",
                                context.Request.Options.CopyMode,
                                qualifiedName,
                                sourceRows);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "CopyMode={Mode}: failed to check {Table}, will re-copy",
                            context.Request.Options.CopyMode,
                            qualifiedName);
                        filteredTables.Add(table);
                    }
                }

                if (filteredTables.Count == 0)
                {
                    return new StageResult(
                        Name,
                        true,
                        TimeSpan.Zero,
                        0,
                            [
                                StageDetail.Skipped(
                                    reason: $"CopyMode={context.Request.Options.CopyMode}: all tables match, nothing to copy")
                            ]);
                }

                logger.LogInformation(
                    "CopyMode={Mode}: {Count} of {Total} tables need re-copy",
                    context.Request.Options.CopyMode,
                    filteredTables.Count,
                    tablesToCopy.Count);

                // TRUNCATE mismatched tables before re-copy to avoid duplicate data
                foreach (var table in filteredTables)
                {
                    var qualifiedName =
                        PgIdentifierQuoter.QuoteSchemaQualified(table.SchemaName, table.Name);
                    try
                    {
                        await destExec.ExecuteNonQueryAsync(
                            $"TRUNCATE TABLE {qualifiedName}",
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "CopyMode={Mode}: failed to truncate {Table}",
                            context.Request.Options.CopyMode,
                            qualifiedName);
                    }
                }

                tablesToCopy = filteredTables;
            }

            // Pre-count total rows across tables that will actually be copied.
            // SkippedTables are already excluded from tablesToCopy at this point.
            long grandTotalRows = 0;
            var rowCountExec = _executorFactory.Create(sourceConn);
            foreach (var t in tablesToCopy)
            {
                if (t.IsPartitioned) continue;
                try
                {
                    var qn = PgIdentifierQuoter.QuoteSchemaQualified(t.SchemaName, t.Name);
                    grandTotalRows += await rowCountExec.ExecuteScalarAsync<long>(
                                          $"SELECT count(*) FROM {qn}",
                                          cancellationToken);
                }
                catch
                {
                    /* table may not be accessible; count as 0 */
                }
            }

            // Cumulative row-based progress tracking across tables
            long completedTableRows = 0;
            string? currentTableName = null;
            long currentTableLastRows = 0;
            var copySw = Stopwatch.StartNew();

            // Map row progress into this stage's band of the overall pipeline percent
            // so the bar never jumps backwards (pipeline reports stageIndex/totalStages
            // at start and (stageIndex+1)/totalStages at completion).
            var stageIndex = context.StageResults.Count;
            var totalStages = Math.Max(context.TotalStages, 1);
            var stageStartPercent = stageIndex * 100.0 / totalStages;
            var stageBandPercent = 100.0 / totalStages;

            var copier = new Copy.PgDataCopier(
                sourceConn,
                destConn,
                _loggerFactory.CreateLogger<Copy.PgDataCopier>(),
                context.SourceConnectionString,
                context.DestinationConnectionString);
            var tableProgress = new Progress<TableCopyProgress>(tp =>
                {
                    var tableKey = $"{tp.SchemaName}.{tp.TableName}";
                    if (currentTableName != null && currentTableName != tableKey)
                    {
                        // Previous table finished — accumulate its rows
                        completedTableRows += currentTableLastRows;
                        currentTableLastRows = 0;
                    }

                    currentTableName = tableKey;
                    currentTableLastRows = tp.RowsCopied;

                    var cumulativeRows = completedTableRows + tp.RowsCopied;
                    var copyElapsed = copySw.Elapsed.TotalSeconds;
                    var rowFraction = grandTotalRows > 0
                                          ? (double)cumulativeRows / grandTotalRows
                                          : 0;
                    var percent = stageStartPercent + rowFraction * stageBandPercent;

                    context.Progress?.Report(
                        new CopyProgress(
                            ECopyStage.CopyData,
                            context.StageResults.Count,
                            context.TotalStages,
                            percent,
                            context.TotalStopwatch?.Elapsed.TotalSeconds ?? 0,
                            TableProgress: new TableProgress(
                                tableKey,
                                cumulativeRows,
                                grandTotalRows,
                                copyElapsed)));
                });
            var stats = await copier.CopyDataAsync(
                            tablesToCopy,
                            context.Request.Options,
                            tableProgress,
                            cancellationToken);

            // Merge stats
            context.Statistics = context.Statistics with
                                     {
                                         TotalRowsCopied =
                                         context.Statistics.TotalRowsCopied + stats.TotalRowsCopied,
                                         TotalBytesTransferred =
                                         context.Statistics.TotalBytesTransferred
                                         + stats.TotalBytesTransferred,
                                         TablesCopied =
                                         context.Statistics.TablesCopied + stats.TablesCopied,
                                         TablesFailed = stats.TablesFailed
                                     };

            var details = new List<StageDetail>
                              {
                                  StageDetail.Statistic("Rows", stats.TotalRowsCopied),
                                  StageDetail.Statistic("Bytes", stats.TotalBytesTransferred)
                              };

            // Report tables excluded from data copy so the user sees them in the log
            if (context.SkippedTables.Count > 0)
            {
                details.Add(StageDetail.Statistic(
                    "Tables skipped (creation failed)", context.SkippedTables.Count));
            }

            if (context.Request.Options.CopyMode != ECopyMode.Full)
            {
                details.Insert(
                    0,
                    StageDetail.Skipped(
                        reason: $"CopyMode={context.Request.Options.CopyMode}: {tablesToCopy.Count} tables needed re-copy"));
            }

            // Report each failed table as a warning and add to stage details
            foreach (var failed in copier.FailedTables)
            {
                context.Warnings.Add(new CopyWarning(Name, EStageMessageKind.Failed, failed, null));
                details.Add(StageDetail.FailedWarning(failed));
            }

            var success = copier.FailedTables.Count == 0;
            return new StageResult(Name, success, TimeSpan.Zero, stats.TablesCopied, details);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CopyData failed: {Message}", ex.Message);
            return new StageResult(
                Name,
                false,
                TimeSpan.Zero,
                0,
                    [
                        StageDetail.Exception(PgExceptionHelper.GetUserMessage(ex))
                    ]);
        }
    }

    /// <summary>
    /// Validates a connection with SELECT 1. If it fails, forces a reopen.
    /// This catches dead TCP connections that Npgsql still reports as Open.
    /// </summary>
    private static async Task<NpgsqlConnection?> ValidateAndReopenAsync(
        CopyContext context,
        bool isSource,
        ILogger logger,
        CancellationToken ct)
    {
        var side = isSource ? ECompareSide.Source : ECompareSide.Destination;

        try
        {
            var conn = isSource
                           ? await PgConnectionHelper.EnsureSourceOpenAsync(context, logger, ct)
                           : await PgConnectionHelper.EnsureDestinationOpenAsync(
                                 context,
                                 logger,
                                 ct);

            // Probe with a real query to detect dead connections
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.CommandTimeout = 10;
            await cmd.ExecuteScalarAsync(ct);

            return conn;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Side} connection validation failed, forcing reopen", side);

            // Force close and reopen
            try
            {
                var oldConn = isSource
                                  ? context.SourceConnection as NpgsqlConnection
                                  : context.DestinationConnection as NpgsqlConnection;
                if (oldConn is not null)
                {
                    await oldConn.CloseAsync();
                    await oldConn.DisposeAsync();
                }
            }
            catch
            {
                /* best effort */
            }

            // Reset so EnsureOpen creates a fresh connection
            if (isSource)
                context.SourceConnection = null;
            else
                context.DestinationConnection = null;

            try
            {
                return isSource
                           ? await PgConnectionHelper.EnsureSourceOpenAsync(context, logger, ct)
                           : await PgConnectionHelper.EnsureDestinationOpenAsync(
                                 context,
                                 logger,
                                 ct);
            }
            catch (Exception reopenEx)
            {
                logger.LogError(reopenEx, "Failed to reopen {Side} connection", side);
                return null;
            }
        }
    }
}
