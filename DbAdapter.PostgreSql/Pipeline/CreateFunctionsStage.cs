using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using CanonicalModel.Enums;
using CanonicalModel.Models;
using CanonicalModel.DTOs;
using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// Creates functions and procedures on the destination.
/// </summary>
public sealed class CreateFunctionsStage : ICopyStage
{
    private readonly IPgConnectionProvider _connectionProvider;

    private readonly PgDdlGenerator _ddl;

    private readonly IPgExecutorFactory _executorFactory;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateFunctions;

    /// <inheritdoc />
    public int Order => 68;

    public bool IsCritical => false;

    /// <summary>Initializes a new instance.</summary>
    public CreateFunctionsStage(
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

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<CreateFunctionsStage>();
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

        var details = new List<StageDetail>();

        // The privileges are asked for once, before the first CREATE FUNCTION, because a SET clause
        // is checked when the function is created and a refusal is settled by then.
        var refusedParameters = await RequestSetPrivilegesAsync(
                                     context,
                                     model,
                                     executor,
                                     logger,
                                     details,
                                     cancellationToken);

        if (refusedParameters.Count > 0)
            context.Properties[PgSetParameterGrant.RefusedSetParametersKey] = refusedParameters;

        // Disable body checking so functions referencing not-yet-created objects
        // (tables, other functions) can be created without validation errors.
        await executor.ExecuteNonQueryAsync("SET check_function_bodies = OFF", cancellationToken);

        // Create each function individually so one failure (e.g. missing type from an
        // unavailable extension) does not abort the remaining functions.
        var stmts = _ddl.GenerateCreateFunctions(model.Functions);
        var created = 0;
        var failedIndices = new List<int>();
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
                        functionName,
                        PgSetParameterGrant.DescribeFailure(
                            ex,
                            model.Functions[i].SetParameters,
                            refusedParameters)));
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

    /// <summary>
    /// Asks the destination for SET on the parameters the functions declare, and returns the
    /// statements a refused parameter would have needed. Best effort: a refusal is the caller's
    /// problem to report, never an error of this stage.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> RequestSetPrivilegesAsync(
        CopyContext context,
        DatabaseModel model,
        ISqlExecutor executor,
        ILogger logger,
        List<StageDetail> details,
        CancellationToken cancellationToken)
    {
        var candidates = PgSetParameterGrant.CandidateParameters(
            model.Functions.SelectMany(function => function.SetParameters ?? []));

        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0)
            return refused;

        var destinationVersion = context.DestinationCapabilities?.ServerVersion;

        if (!PgSetParameterGrant.SupportsGrants(destinationVersion))
        {
            // A grant is a statement this server does not have; there is nothing to hand an
            // administrator either, because on such a server only a superuser can run the copy.
            logger.LogInformation(
                "Destination {Version} cannot grant SET on a parameter (PostgreSQL {Floor} is the floor), so {Parameters} stay the server's",
                destinationVersion ?? "unknown",
                PgSetParameterGrant.MinimumServerVersion,
                string.Join(", ", candidates));
            details.Add(StageDetail.Statistic("SET privileges granted", 0));
            return refused;
        }

        var outcomes = await PgSetParameterGrant.GrantAsync(
                           executor,
                           candidates,
                           logger,
                           cancellationToken);

        foreach (var outcome in outcomes)
        {
            if (outcome.Granted)
                logger.LogInformation("SET privilege granted: {Statement}", outcome.Statement);
            else
                logger.LogWarning(
                    "SET privilege refused: {Statement}: {Error}",
                    outcome.Statement,
                    outcome.Reason);
        }

        foreach (var outcome in outcomes.Where(outcome => !outcome.Granted))
            refused[outcome.Parameter] = outcome.Statement;

        details.Add(
            StageDetail.Statistic("SET privileges granted", outcomes.Count(outcome => outcome.Granted)));

        return refused;
    }
}
