using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;
using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// Retries functions that failed during the initial CreateFunctions stage.
/// Runs after tables/constraints exist so functions referencing table types succeed.
/// </summary>
public sealed class RetryFunctionsStage : ICopyStage
{
    private readonly IPgConnectionProvider _connectionProvider;

    private readonly PgDdlGenerator _ddl;

    private readonly IPgExecutorFactory _executorFactory;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.RetryFunctions;

    /// <inheritdoc />
    public int Order => 115;

    public bool IsCritical => false;

    /// <summary>Initializes a new instance.</summary>
    public RetryFunctionsStage(
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

        if (!context.Request.Options.CopyFunctions)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopyFunctions=false")]);

        // Retrieve failed function indices stored by CreateFunctionsStage
        if (!context.Properties.TryGetValue("FailedFunctionIndices", out var obj)
            || obj is not List<int> failedIndices || failedIndices.Count == 0)
        {
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "No functions to retry")]);
        }

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<RetryFunctionsStage>();
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

        var stmts = _ddl.GenerateCreateFunctions(model.Functions);
        var created = 0;
        var details = new List<StageDetail>();

        foreach (var idx in failedIndices)
        {
            if (idx >= model.Functions.Count || idx >= stmts.Count)
                continue;

            var functionName = $"{model.Functions[idx].SchemaName}.{model.Functions[idx].Name}";
            try
            {
                await executor.ExecuteNonQueryAsync(stmts[idx], cancellationToken);
                created++;
                details.Add(StageDetail.Created(functionName));
            }
            catch (Exception ex)
            {
                var userMessage = PgExceptionHelper.GetUserMessage(ex);
                logger.LogWarning(
                    ex,
                    "Retry failed for function {Function}: {Error}",
                    functionName,
                    ex.Message);

                // One function the server refused is one object lost, not a failed run:
                // the warning names it and the run still completes.
                context.Warnings.Add(
                    new CopyWarning(
                        Name,
                        EStageMessageKind.Failed,
                        functionName,
                        new Dictionary<string, object> { [PropKeys.Reason] = userMessage }));
                details.Add(StageDetail.FailedWarning(functionName, userMessage));
            }
        }

        if (created > 0)
            context.Statistics = context.Statistics with
                                     {
                                         FunctionsCopied =
                                         context.Statistics.FunctionsCopied + created
                                     };

        var stillFailed = failedIndices.Count - created;
        details.Add(StageDetail.Summary(failedIndices.Count, created, stillFailed));

        // RetryFunctions is the single source of truth for function creation losses,
        // and a loss is reported to the user as a warning naming the function.
        return new StageResult(
            Name,
            true,
            TimeSpan.Zero,
            created,
            details);
    }
}
