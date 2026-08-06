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
/// Creates enum, domain, and composite types on the destination.
/// </summary>
public sealed class CreateTypesStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateTypes;

    /// <inheritdoc />
    public int Order => 65;

    /// <summary>Initializes a new instance.</summary>
    public CreateTypesStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
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

        var model = context.SourceModel!;

        // Enums and domains have no intra-stage dependencies; create them directly,
        // in the order computed by the dependency analysis.
        var simpleStmts = new List<(EDatabaseObjectType ObjType, string TypeName, string Sql)>();
        simpleStmts.AddRange(
            model.Enums.Select(e => (EDatabaseObjectType.Enum, $"{e.SchemaName}.{e.Name}",
                                        _ddl.GenerateCreateEnums([e]).Single())));
        simpleStmts.AddRange(
            model.Domains.Select(d => (EDatabaseObjectType.Domain, $"{d.SchemaName}.{d.Name}",
                                          _ddl.GenerateCreateDomains([d]).Single())));
        simpleStmts =
            [
                .. DependencyOrdering.Sort(
                    simpleStmts,
                    context.DependencyResult,
                    s => (s.ObjType, s.TypeName))
            ];

        if (simpleStmts.Count == 0 && model.CompositeTypes.Count == 0)
            return new StageResult(Name, true, TimeSpan.Zero, 0, [StageDetail.Skipped(reason: "No types")]);

        var logger = _loggerFactory.CreateLogger<CreateTypesStage>();
        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        // Create each type individually so one failure does not abort the rest.
        // Types already present (e.g. created by an extension such as
        // pg_stat_statements) are silently skipped.
        var created = 0;
        var skipped = 0;
        var details = new List<StageDetail>();
        foreach (var (_, typeName, sql) in simpleStmts)
        {
            try
            {
                await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                created++;
            }
            catch (PostgresException ex) when (ex.SqlState == "42710") // duplicate_object
            {
                skipped++;
                details.Add(StageDetail.Skipped(typeName, "already exists"));
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to create type {Type}: {Error}",
                    typeName,
                    ex.Message);
                context.Warnings.Add(
                    new CopyWarning(
                        Name,
                        EStageMessageKind.Failed,
                        typeName,
                        new Dictionary<string, object> { [PropKeys.Reason] = PgExceptionHelper.GetUserMessage(ex) }));
                details.Add(
                    StageDetail.Failed(typeName, PgExceptionHelper.GetUserMessage(ex)));
            }
        }

        // Composite types can reference other composite types. Creation order is
        // driven by the dependency analysis (referenced types first); the retry
        // loop remains a safety net for references the analysis could not see
        // (e.g. types resolved through search_path at runtime).
        IReadOnlyList<(string TypeName, string Sql)> pending = DependencyOrdering.Sort(
            model.CompositeTypes
                .Select(t => (TypeName: $"{t.SchemaName}.{t.Name}",
                                 Sql: _ddl.GenerateCreateCompositeTypes([t]).Single()))
                .ToList(),
            context.DependencyResult,
            t => (EDatabaseObjectType.CompositeType, t.TypeName));
        var lastErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var madeProgress = true;
        while (pending.Count > 0 && madeProgress)
        {
            madeProgress = false;
            var retry = new List<(string TypeName, string Sql)>();
            foreach (var (typeName, sql) in pending)
            {
                try
                {
                    await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                    created++;
                    madeProgress = true;
                }
                catch (PostgresException ex) when (ex.SqlState == "42710") // duplicate_object
                {
                    skipped++;
                    madeProgress = true;
                    details.Add(StageDetail.Skipped(typeName, "already exists"));
                }
                catch (Exception ex)
                {
                    // May reference a composite type not yet created; retry next pass.
                    lastErrors[typeName] = PgExceptionHelper.GetUserMessage(ex);
                    retry.Add((typeName, sql));
                }
            }

            pending = retry;
        }

        // Anything still failing after no further progress is a genuine failure.
        foreach (var (typeName, _) in pending)
        {
            var message = lastErrors.GetValueOrDefault(typeName, "unknown error");
            logger.LogWarning("Failed to create composite type {Type}: {Error}", typeName, message);
            context.Warnings.Add(
                new CopyWarning(Name, EStageMessageKind.Failed, typeName,
                    new Dictionary<string, object> { [PropKeys.Reason] = message }));
            details.Add(StageDetail.Failed(typeName, message));
        }

        if (model.Enums.Count > 0) details.Add(StageDetail.Count(EDatabaseObjectType.Enum, model.Enums.Count));
        if (model.Domains.Count > 0)
            details.Add(StageDetail.Count(EDatabaseObjectType.Domain, model.Domains.Count));
        if (model.CompositeTypes.Count > 0)
            details.Add(StageDetail.Count(EDatabaseObjectType.CompositeType, model.CompositeTypes.Count));
        if (skipped > 0) details.Add(StageDetail.Statistic("Already existed", skipped));

        var total = simpleStmts.Count + model.CompositeTypes.Count;
        var failed = total - created - skipped;
        var success = failed == 0;
        return new StageResult(
            Name,
            success,
            TimeSpan.Zero,
            created,
            details);
    }
}
