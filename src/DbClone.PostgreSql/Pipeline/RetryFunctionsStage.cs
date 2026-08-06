using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Retries functions that failed during the initial CreateFunctions stage.
/// Runs after tables/constraints exist so functions referencing table types succeed.
/// </summary>
public sealed class RetryFunctionsStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.RetryFunctions;

    /// <inheritdoc />
    public int Order => 115;

    /// <summary>Initializes a new instance.</summary>
    public RetryFunctionsStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
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
        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());

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
                logger.LogWarning(
                    ex,
                    "Retry failed for function {Function}: {Error}",
                    functionName,
                    ex.Message);
                details.Add(
                    StageDetail.Failed(functionName, PgExceptionHelper.GetUserMessage(ex)));
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

        // RetryFunctions is the single source of truth for function creation errors.
        // Only report failure if functions remain unrecoverable after this final attempt.
        return new StageResult(
            Name,
            stillFailed == 0,
            TimeSpan.Zero,
            created,
            details);
    }
}
