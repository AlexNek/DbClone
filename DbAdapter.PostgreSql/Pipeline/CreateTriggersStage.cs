using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;
using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// Creates triggers on the destination.
/// </summary>
public sealed class CreateTriggersStage : ICopyStage
{
    private readonly IPgConnectionProvider _connectionProvider;

    private readonly PgDdlGenerator _ddl;

    private readonly IPgExecutorFactory _executorFactory;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateTriggers;

    /// <inheritdoc />
    public int Order => 140;

    public bool IsCritical => false;

    /// <summary>Initializes a new instance.</summary>
    public CreateTriggersStage(
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

        if (!context.Request.Options.CopyTriggers)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopyTriggers=false")]);

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<CreateTriggersStage>();
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

        var executor = _executorFactory.Create(conn);

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
                var userMessage = PgExceptionHelper.GetUserMessage(ex);
                logger.LogWarning(
                    ex,
                    "Failed to create trigger {Trigger}: {Error}",
                    triggerName,
                    ex.Message);

                // One refused trigger is one object lost, not a failed run.
                context.Warnings.Add(
                    new CopyWarning(
                        Name,
                        EStageMessageKind.Failed,
                        triggerName,
                        new Dictionary<string, object> { [PropKeys.Reason] = userMessage }));
                details.Add(StageDetail.FailedWarning(triggerName, userMessage));
            }
        }

        context.Statistics = context.Statistics with { TriggersCopied = created };
        details.Add(StageDetail.Summary(stmts.Count, created, stmts.Count - created));
        return new StageResult(Name, true, TimeSpan.Zero, created, details);
    }
}
