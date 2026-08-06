using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates functions and procedures on the destination.
/// </summary>
public sealed class CreateFunctionsStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateFunctions;

    /// <inheritdoc />
    public int Order => 68;

    /// <summary>Initializes a new instance.</summary>
    public CreateFunctionsStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
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

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<CreateFunctionsStage>();
        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        // Disable body checking so functions referencing not-yet-created objects
        // (tables, other functions) can be created without validation errors.
        await executor.ExecuteNonQueryAsync("SET check_function_bodies = OFF", cancellationToken);

        // Create each function individually so one failure (e.g. missing type from an
        // unavailable extension) does not abort the remaining functions.
        var stmts = _ddl.GenerateCreateFunctions(model.Functions);
        var created = 0;
        var failedIndices = new List<int>();
        var details = new List<StageDetail>();
        for (var i = 0; i < stmts.Count; i++)
        {
            var functionName = $"{model.Functions[i].SchemaName}.{model.Functions[i].Name}";
            try
            {
                await executor.ExecuteNonQueryAsync(stmts[i], cancellationToken);
                created++;
                details.Add(StageDetail.Created(functionName));
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to create function {Function}: {Error}",
                    functionName,
                    ex.Message);
                failedIndices.Add(i);
                details.Add(
                    StageDetail.Deferred(
                        functionName, PgExceptionHelper.GetUserMessage(ex)));
            }
        }

        await executor.ExecuteNonQueryAsync("SET check_function_bodies = ON", cancellationToken);

        // Store failed indices for RetryFunctionsStage (runs after tables exist)
        if (failedIndices.Count > 0)
            context.Properties["FailedFunctionIndices"] = failedIndices;

        context.Statistics = context.Statistics with { FunctionsCopied = created };

        if (failedIndices.Count > 0)
            details.Add(
                StageDetail.Summary(stmts.Count, created, failedIndices.Count));

        // Return Success = true even with failures: RetryFunctionsStage will attempt
        // recovery and is the single source of truth for the final error count.
        return new StageResult(Name, Success: true, TimeSpan.Zero, created, details);
    }
}
