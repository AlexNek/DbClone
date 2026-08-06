using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Models;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Re-copies only the tables that had row count mismatches during validation.
/// Runs after <see cref="ValidateStage"/> and retries failed tables.
/// </summary>
public sealed class ReCopyMismatchedStage : ICopyStage
{
    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.ReCopyMismatched;

    /// <inheritdoc />
    public int Order => 155;

    /// <summary>Initializes a new instance.</summary>
    public ReCopyMismatchedStage(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

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

        if (context.MismatchedTables.Count == 0)
        {
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "No mismatched tables")]);
        }

        var logger = _loggerFactory.CreateLogger<ReCopyMismatchedStage>();
        logger.LogInformation(
            "Re-copying {Count} mismatched table(s): {Tables}",
            context.MismatchedTables.Count,
            string.Join(", ", context.MismatchedTables));

        // Validate connections — they may have been disposed by PgDataCopier
        var sourceConn = await PgConnectionHelper.ValidateAndReopenAsync(
                             context,
                             isSource: true,
                             logger,
                             cancellationToken);
        var destConn = await PgConnectionHelper.ValidateAndReopenAsync(
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

        // Validate connections
        var sourceExec = new PgSqlExecutor(
            sourceConn,
            _loggerFactory.CreateLogger<PgSqlExecutor>());
        var destExec = new PgSqlExecutor(destConn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        // Find the TableDefinitions for mismatched tables
        var model = context.SourceModel!;
        var mismatchedDefs = new List<TableDefinition>();

        foreach (var qualifiedName in context.MismatchedTables)
        {
            // Parse "schema.table" back to schema + name
            var parts = qualifiedName.Split('.', 2);
            var schemaName = parts.Length == 2 ? parts[0].Trim('"') : "public";
            var tableName = parts.Length == 2 ? parts[1].Trim('"') : parts[0].Trim('"');

            var tableDef = model.Tables.FirstOrDefault(t =>
                string.Equals(t.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));

            if (tableDef is not null)
            {
                mismatchedDefs.Add(tableDef);
            }
            else
            {
                logger.LogWarning(
                    "Could not find table definition for {Table}, skipping re-copy",
                    qualifiedName);
            }
        }

        if (mismatchedDefs.Count == 0)
        {
            return new StageResult(
                Name,
                false,
                TimeSpan.Zero,
                0,
                    [
                        StageDetail.Exception(
                            "No matching table definitions found for mismatched tables")
                    ]);
        }

        // TRUNCATE mismatched tables in destination before re-copy
        foreach (var table in mismatchedDefs)
        {
            var qualifiedName =
                PgIdentifierQuoter.QuoteSchemaQualified(table.SchemaName, table.Name);
            try
            {
                await destExec.ExecuteNonQueryAsync(
                    $"TRUNCATE TABLE {qualifiedName}",
                    cancellationToken);
                logger.LogInformation("Truncated {Table} for re-copy", qualifiedName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to truncate {Table}, proceeding with re-copy",
                    qualifiedName);
            }
        }

        // Re-copy using PgDataCopier with only the mismatched tables
        var copier = new Copy.PgDataCopier(
            sourceConn,
            destConn,
            _loggerFactory.CreateLogger<Copy.PgDataCopier>(),
            context.SourceConnectionString,
            context.DestinationConnectionString);

        // Map row progress into this stage's band of the overall pipeline percent
        // so the bar never jumps backwards.
        var stageIndex = context.StageResults.Count;
        var totalStages = Math.Max(context.TotalStages, 1);
        var stageStartPercent = stageIndex * 100.0 / totalStages;
        var stageBandPercent = 100.0 / totalStages;
        var totalTables = Math.Max(mismatchedDefs.Count, 1);
        var currentTableIndex = -1;
        string? currentTable = null;

        var reCopyProgress = new Progress<TableCopyProgress>(tp =>
            {
                var tableKey = $"{tp.SchemaName}.{tp.TableName}";
                if (currentTable != null && currentTable != tableKey)
                    currentTableIndex++;
                currentTable = tableKey;

                var rowFraction = tp.TotalRows > 0
                                      ? (double)tp.RowsCopied / tp.TotalRows
                                      : 0;
                var overallFraction = (currentTableIndex + rowFraction) / totalTables;

                context.Progress?.Report(
                    new CopyProgress(
                        ECopyStage.CopyData,
                        context.StageResults.Count,
                        context.TotalStages,
                        stageStartPercent + overallFraction * stageBandPercent,
                        context.TotalStopwatch?.Elapsed.TotalSeconds ?? 0,
                        TableProgress: new TableProgress(
                            tableKey,
                            tp.RowsCopied,
                            tp.TotalRows,
                            tp.Elapsed.TotalSeconds)));
            });
        var stats = await copier.CopyDataAsync(
                        mismatchedDefs,
                        context.Request.Options,
                        reCopyProgress,
                        cancellationToken);

        // Update statistics
        context.Statistics = context.Statistics with
                                 {
                                     TotalRowsCopied =
                                     context.Statistics.TotalRowsCopied + stats.TotalRowsCopied,
                                     TablesCopied =
                                     context.Statistics.TablesCopied + stats.TablesCopied,
                                     TablesFailed = stats.TablesFailed
                                 };

        // Re-validate connections after PgDataCopier (it may have disposed the originals)
        var recheckSource = await PgConnectionHelper.ValidateAndReopenAsync(
                                context,
                                isSource: true,
                                logger,
                                cancellationToken);
        var recheckDest = await PgConnectionHelper.ValidateAndReopenAsync(
                              context,
                              isSource: false,
                              logger,
                              cancellationToken);

        if (recheckSource is null || recheckDest is null)
        {
            // Can't re-validate — report all as mismatched
            return new StageResult(
                Name,
                false,
                TimeSpan.Zero,
                0,
                    [
                        StageDetail.ConnectionFailed(recheckSource is null ? ECompareSide.Source : ECompareSide.Destination)
                    ]);
        }

        var recheckSourceExec = new PgSqlExecutor(
            recheckSource,
            _loggerFactory.CreateLogger<PgSqlExecutor>());
        var recheckDestExec = new PgSqlExecutor(
            recheckDest,
            _loggerFactory.CreateLogger<PgSqlExecutor>());

        // Re-validate the re-copied tables
        var details = new List<StageDetail>();
        var stillMismatched = 0;

        foreach (var table in mismatchedDefs)
        {
            var qualifiedName =
                PgIdentifierQuoter.QuoteSchemaQualified(table.SchemaName, table.Name);
            try
            {
                var sourceRows = await recheckSourceExec.ExecuteScalarAsync<long>(
                                     $"SELECT count(*) FROM {qualifiedName}",
                                     cancellationToken);
                var destRows = await recheckDestExec.ExecuteScalarAsync<long>(
                                   $"SELECT count(*) FROM {qualifiedName}",
                                   cancellationToken);

                if (sourceRows == destRows)
                {
                    details.Add(StageDetail.Fixed(qualifiedName, destRows));
                }
                else
                {
                    stillMismatched++;
                    details.Add(StageDetail.StillMismatched(qualifiedName, sourceRows, destRows));
                    context.Errors.Add(new CopyError(Name, EStageMessageKind.StillMismatched, qualifiedName,
                        new Dictionary<string, object>
                        {
                            [PropKeys.SourceRows] = sourceRows,
                            [PropKeys.DestRows] = destRows
                        }, null));
                }
            }
            catch (Exception ex)
            {
                stillMismatched++;
                details.Add(StageDetail.Exception(PgExceptionHelper.GetUserMessage(ex)));
            }
        }

        // Clear mismatched tables that are now fixed
        context.MismatchedTables.Clear();

        // Report failed tables from re-copy
        foreach (var failed in copier.FailedTables)
        {
            details.Add(StageDetail.FailedWarning(failed));
            context.Warnings.Add(new CopyWarning(Name, EStageMessageKind.Failed, failed, null));
        }

        var isValid = stillMismatched == 0 && copier.FailedTables.Count == 0;
        details.Add(StageDetail.Summary(mismatchedDefs.Count - stillMismatched, stillMismatched));

        return new StageResult(Name, isValid, TimeSpan.Zero, mismatchedDefs.Count, details);
    }
}
