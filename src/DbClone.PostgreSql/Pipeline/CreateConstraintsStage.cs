using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates foreign keys, unique constraints, and check constraints on the destination.
/// </summary>
public sealed class CreateConstraintsStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateConstraints;

    /// <inheritdoc />
    public int Order => 100;

    /// <summary>Initializes a new instance.</summary>
    public CreateConstraintsStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
    {
        _ddl = ddl;
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

        if (!context.Request.Options.CopyConstraints)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopyConstraints=false")]);

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<CreateConstraintsStage>();
        var conn = await PgConnectionHelper.EnsureDestinationOpenAsync(
                       context,
                       logger,
                       cancellationToken);
        var executor = new PgSqlExecutor(
            conn,
            _loggerFactory.CreateLogger<PgSqlExecutor>(),
            TimeSpan.FromMinutes(5));

        var totalConstraints = 0;
        var failedConstraints = 0;
        foreach (var table in model.Tables)
        {
            var fkStmts = _ddl.GenerateForeignKeys(table.ForeignKeys, table.SchemaName, table.Name);
            foreach (var sql in fkStmts)
            {
                try
                {
                    await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                    totalConstraints++;
                }
                catch (Exception ex)
                {
                    failedConstraints++;
                    logger.LogWarning(
                        ex,
                        "Failed to create constraint on {Table}: {Error}",
                        $"{table.SchemaName}.{table.Name}",
                        ex.Message);
                    context.Warnings.Add(
                        new CopyWarning(
                            Name,
                            EStageMessageKind.Failed,
                            $"{table.SchemaName}.{table.Name}",
                            new Dictionary<string, object> { [PropKeys.Reason] = PgExceptionHelper.GetUserMessage(ex) }));
                }
            }
        }

        context.Statistics = context.Statistics with
                                 {
                                     ConstraintsCreated =
                                     context.Statistics.ConstraintsCreated + totalConstraints
                                 };

        var summaryDetail = StageDetail.Summary(totalConstraints, failedConstraints);

        return new StageResult(Name, true, TimeSpan.Zero, totalConstraints, [summaryDetail]);
    }
}
