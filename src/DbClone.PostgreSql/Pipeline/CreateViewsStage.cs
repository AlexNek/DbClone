using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.DependencyAnalysis;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates views and materialized views on the destination.
/// Regular views are created first (dependency-ordered), then materialized views.
/// </summary>
public sealed class CreateViewsStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateViews;

    /// <inheritdoc />
    public int Order => 120;

    /// <summary>Initializes a new instance.</summary>
    public CreateViewsStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
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

        if (!context.Request.Options.CopyViews)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopyViews=false")]);

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<CreateViewsStage>();
        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        var created = 0;
        var details = new List<StageDetail>();

        // ── Regular views (dependency-ordered with retry) ──────────────────────
        var stmts = _ddl.GenerateCreateViews(model.Views);
        var views = new List<(string ViewName, string Sql)>();
        for (var i = 0; i < stmts.Count; i++)
            views.Add(($"{model.Views[i].SchemaName}.{model.Views[i].Name}", stmts[i]));

        IReadOnlyList<(string ViewName, string Sql)> pending = DependencyOrdering.Sort(
            views,
            context.DependencyResult,
            v => (EDatabaseObjectType.View, v.ViewName));

        var lastErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var madeProgress = true;
        while (pending.Count > 0 && madeProgress)
        {
            madeProgress = false;
            var retry = new List<(string ViewName, string Sql)>();
            foreach (var (viewName, sql) in pending)
            {
                try
                {
                    await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                    created++;
                    madeProgress = true;
                    details.Add(StageDetail.Created(viewName));
                }
                catch (Exception ex)
                {
                    // May reference a view/object not yet created; retry next pass.
                    lastErrors[viewName] = PgExceptionHelper.GetUserMessage(ex);
                    retry.Add((viewName, sql));
                }
            }

            pending = retry;
        }

        // Anything still failing after no further progress is a genuine failure.
        foreach (var (viewName, _) in pending)
        {
            var message = lastErrors.GetValueOrDefault(viewName, "unknown error");
            logger.LogError("Failed to create view {View}: {Error}", viewName, message);
            context.Errors.Add(
                new CopyError(Name, EStageMessageKind.Failed, viewName,
                    new Dictionary<string, object> { [PropKeys.Reason] = message }, null));
            details.Add(StageDetail.Failed(viewName, message));
        }

        // ── Materialized views (after regular views — matviews may reference them) ──
        var matStmts = _ddl.GenerateCreateMaterializedViews(model.MaterializedViews);
        for (var i = 0; i < matStmts.Count; i++)
        {
            var mv = model.MaterializedViews[i];
            var mvName = $"{mv.SchemaName}.{mv.Name}";
            try
            {
                await executor.ExecuteNonQueryAsync(matStmts[i], cancellationToken);
                created++;
                details.Add(StageDetail.Created(mvName));
            }
            catch (Exception ex)
            {
                var message = PgExceptionHelper.GetUserMessage(ex);
                logger.LogError(
                    "Failed to create materialized view {MatView}: {Error}", mvName, message);
                context.Errors.Add(
                    new CopyError(Name, EStageMessageKind.Failed, mvName,
                        new Dictionary<string, object> { [PropKeys.Reason] = message }, ex));
                details.Add(StageDetail.Failed(mvName, message));
            }
        }

        var totalExpected = stmts.Count + matStmts.Count;
        context.Statistics = context.Statistics with { ViewsCopied = created };
        return new StageResult(Name, created == totalExpected, TimeSpan.Zero, created, details);
    }
}
