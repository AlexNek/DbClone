using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates secondary (non-primary-key) indexes on the destination after data copy.
/// Primary key indexes are always created inline with CREATE TABLE and are not
/// affected by the CopyIndexes option.
/// </summary>
public sealed class CreateIndexesStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly IPgExecutorFactory _executorFactory;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateIndexes;

    /// <inheritdoc />
    public int Order => 95;

    /// <summary>Initializes a new instance.</summary>
    public CreateIndexesStage(PgDdlGenerator ddl, IPgExecutorFactory executorFactory, ILoggerFactory loggerFactory)
    {
        _ddl = ddl;
        _executorFactory = executorFactory;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Request.Options.CopyMode is ECopyMode.Resume or ECopyMode.Update)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopyMode=Resume/Update")]);

        if (!context.Request.Options.CopyIndexes)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [
                        StageDetail.Skipped(
                            reason: "CopyIndexes=false — primary key indexes were still created with table structures")
                    ]);

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<CreateIndexesStage>();
        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = _executorFactory.Create(conn);

        var totalIndexes = 0;
        var failedIndexes = 0;
        var skippedIndexes = 0;
        var details = new List<StageDetail>();

        foreach (var table in model.Tables)
        {
            // Tables that failed to create → their indexes cannot be created either.
            // Report explicitly as skipped so the user sees why the count differs.
            if (context.SkippedTables.Contains(
                    new Application.Models.TableId(table.SchemaName, table.Name)))
            {
                foreach (var idx in table.Indexes.Where(i => !i.IsPrimary))
                {
                    skippedIndexes++;
                    var idxName = $"{table.SchemaName}.{table.Name}.{idx.Name}";
                    details.Add(StageDetail.SkippedError(idxName, "parent table was not created"));
                }

                continue;
            }

            var indexStmts = _ddl.GenerateCreateIndexes(
                table.Indexes,
                table.SchemaName,
                table.Name);
            foreach (var sql in indexStmts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                    totalIndexes++;
                }
                catch (Exception ex)
                {
                    failedIndexes++;
                    var userMsg = PgExceptionHelper.GetUserMessage(ex);
                    logger.LogWarning(
                        ex,
                        "Failed to create index on {Table}: {Error}",
                        $"{table.SchemaName}.{table.Name}",
                        ex.Message);
                    context.Warnings.Add(
                        new CopyWarning(
                            Name,
                            EStageMessageKind.Failed,
                            $"{table.SchemaName}.{table.Name}",
                            new Dictionary<string, object> { [PropKeys.Reason] = userMsg }));
                    details.Add(StageDetail.Failed($"{table.SchemaName}.{table.Name}", userMsg));
                }
            }
        }

        context.Statistics = context.Statistics with
                                 {
                                     IndexesCreated =
                                     context.Statistics.IndexesCreated + totalIndexes
                                 };

        var processedTotal = totalIndexes + failedIndexes + skippedIndexes;
        details.Insert(0, StageDetail.Summary(processedTotal, totalIndexes, failedIndexes, skippedIndexes));

        var success = failedIndexes == 0 && skippedIndexes == 0;
        return new StageResult(
            Name,
            success,
            TimeSpan.Zero,
            processedTotal,
            details);
    }
}
