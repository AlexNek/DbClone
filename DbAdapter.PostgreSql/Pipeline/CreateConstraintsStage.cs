using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.Execution;

using Microsoft.Extensions.Logging;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;
using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// Creates foreign keys, unique constraints, and check constraints on the destination.
/// A foreign key is created only while the table it references is inside the copied scope;
/// the rest are reported once per parent table and never sent to the server.
/// </summary>
public sealed class CreateConstraintsStage : ICopyStage
{
    private readonly IPgConnectionProvider _connectionProvider;

    private readonly PgDdlGenerator _ddl;

    private readonly IPgExecutorFactory _executorFactory;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateConstraints;

    /// <inheritdoc />
    public int Order => 100;

    public bool IsCritical => false;

    /// <summary>Initializes a new instance.</summary>
    public CreateConstraintsStage(
        PgDdlGenerator ddl,
        IPgExecutorFactory executorFactory,
        IPgConnectionProvider connectionProvider,
        ILoggerFactory loggerFactory)
    {
        _ddl = ddl;
        _executorFactory = executorFactory;
        _connectionProvider = connectionProvider;
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

        // One predicate decides scope for the whole stage: a constraint may be created only
        // while its parent table is in the copied model and was created. Anything else is a
        // consequence of the user's own scope choice, never an attempt and never a failure.
        var plan = ForeignKeyScopePlan.Build(model, context.SkippedTables);

        var createdConstraints = 0;
        var failedConstraints = 0;
        var details = new List<StageDetail>();
        ISqlExecutor? executor = null;

        foreach (var group in plan.InScope)
        {
            var fkStmts = _ddl.GenerateForeignKeys(
                group.ForeignKeys,
                group.Table.Schema,
                group.Table.Name);

            if (fkStmts.Count == 0)
                continue;

            // Defer connection validation until there is a statement to run, the way the
            // index stage does, so a run whose constraints are all out of scope needs no
            // round-trip at all.
            if (executor is null)
            {
                var conn = await _connectionProvider.GetDestinationConnectionAsync(
                               context,
                               logger,
                               cancellationToken);
                if (conn is null)
                    return new StageResult(
                        Name,
                        false,
                        TimeSpan.Zero,
                        0,
                            [StageDetail.ConnectionFailed(ECompareSide.Destination)]);

                executor = _executorFactory.Create(conn);
            }

            foreach (var sql in fkStmts)
            {
                try
                {
                    await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                    createdConstraints++;
                }
                catch (Exception ex)
                {
                    failedConstraints++;
                    logger.LogWarning(
                        ex,
                        "Failed to create constraint on {Table}: {Error}",
                        group.Table.FullName,
                        ex.Message);
                    context.Warnings.Add(
                        new CopyWarning(
                            Name,
                            EStageMessageKind.Failed,
                            group.Table.FullName,
                            new Dictionary<string, object> { [PropKeys.Reason] = PgExceptionHelper.GetUserMessage(ex) }));
                }
            }
        }

        // A scope consequence is stated once per parent table, not once per constraint, so
        // eighteen constraints into one missing table read as one line naming that table.
        foreach (var deferred in plan.Deferred)
        {
            var reason = $"{deferred.Count} foreign key(s) not created — parent table is outside the copied scope";

            details.Add(
                new StageDetail(
                    ELogLevel.Info,
                    EStageMessageKind.Skipped,
                    deferred.Parent.FullName,
                    new Dictionary<string, object>
                    {
                        [PropKeys.Count] = deferred.Count,
                        [PropKeys.Reason] = reason
                    }));

            context.Warnings.Add(
                new CopyWarning(
                    Name,
                    EStageMessageKind.Skipped,
                    deferred.Parent.FullName,
                    new Dictionary<string, object> { [PropKeys.Reason] = reason }));
        }

        context.Statistics = context.Statistics with
                                 {
                                     ConstraintsCreated =
                                     context.Statistics.ConstraintsCreated + createdConstraints
                                 };

        var totalConstraints = createdConstraints + failedConstraints + plan.DeferredCount;
        details.Insert(
            0,
            StageDetail.Summary(totalConstraints, createdConstraints, failedConstraints, plan.DeferredCount));

        return new StageResult(Name, true, TimeSpan.Zero, totalConstraints, details);
    }
}
