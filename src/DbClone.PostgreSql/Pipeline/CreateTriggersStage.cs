using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates triggers on the destination.
/// </summary>
public sealed class CreateTriggersStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateTriggers;

    /// <inheritdoc />
    public int Order => 140;

    /// <summary>Initializes a new instance.</summary>
    public CreateTriggersStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
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

        if (!context.Request.Options.CopyTriggers)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopyTriggers=false")]);

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<CreateTriggersStage>();
        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        // Create each trigger individually so one failure (e.g. missing table or
        // function) does not abort the remaining triggers.
        var stmts = _ddl.GenerateCreateTriggers(model.Triggers);
        var created = 0;
        var details = new List<StageDetail>();
        for (var i = 0; i < stmts.Count; i++)
        {
            var triggerName = $"{model.Triggers[i].SchemaName}.{model.Triggers[i].Name}";
            try
            {
                await executor.ExecuteNonQueryAsync(stmts[i], cancellationToken);
                created++;
                details.Add(StageDetail.Created(triggerName));
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to create trigger {Trigger}: {Error}",
                    triggerName,
                    ex.Message);
                context.Errors.Add(
                    new CopyError(
                        Name,
                        EStageMessageKind.Failed,
                        triggerName,
                        new Dictionary<string, object> { [PropKeys.Reason] = PgExceptionHelper.GetUserMessage(ex) }, ex));
                details.Add(
                    StageDetail.Failed(
                        triggerName, PgExceptionHelper.GetUserMessage(ex)));
            }
        }

        context.Statistics = context.Statistics with { TriggersCopied = created };
        return new StageResult(Name, created == stmts.Count, TimeSpan.Zero, created, details);
    }
}
