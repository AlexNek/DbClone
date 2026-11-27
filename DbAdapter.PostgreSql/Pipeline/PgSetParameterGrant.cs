using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Execution;
using DbAdapter.PostgreSql.Metadata;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// The one policy for a privilege the copy may answer for itself.
/// <para>
/// A function's <c>SET</c> clause is checked when the function is created, and a role that is not
/// allowed to set the parameter cannot store the clause. Five settings in one list are safe to ask
/// for, because their only effect is what the server writes to its log; anything else could change
/// what a function is allowed to do, so a foreign database can never make the copy grant it.
/// </para>
/// <para>
/// A refusal is not a failure: the caller attempts its objects anyway, and the statement a refused
/// parameter would have needed is kept so the report can name it for an administrator.
/// </para>
/// </summary>
internal static class PgSetParameterGrant
{
    /// <summary>The first server version that accepts <c>GRANT … ON PARAMETER</c>.</summary>
    public const int MinimumServerVersion = 15;

    /// <summary>
    /// Where <see cref="CreateFunctionsStage"/> leaves the refused parameters for
    /// <see cref="RetryFunctionsStage"/>, so the warning that counts a lost object can state the
    /// statement that would have saved it.
    /// </summary>
    public const string RefusedSetParametersKey = "RefusedSetParameters";

    /// <summary>
    /// The only settings the copy ever asks for: every one of them changes what the server logs,
    /// and nothing else.
    /// </summary>
    public static IReadOnlyList<string> AllowedParameters { get; } =
        [
            "log_min_messages",
            "log_min_error_statement",
            "log_min_duration_statement",
            "log_duration",
            "log_statement",
        ];

    /// <summary>
    /// The declared parameters the copy may ask for: distinct, trimmed, and inside the allow-list.
    /// A parameter the list does not name is never sent to the server.
    /// </summary>
    public static IReadOnlyList<string> CandidateParameters(IEnumerable<string>? declaredParameters)
    {
        if (declaredParameters is null)
            return [];

        return declaredParameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter))
            .Select(parameter => parameter.Trim())
            .Where(IsAllowed)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// True when the destination is new enough for the statement to exist. The destination decides,
    /// never the source: the grant runs on the server that creates the function. An unreadable
    /// version buys no attempt, because the statement would fail on every parameter.
    /// </summary>
    public static bool SupportsGrants(string? destinationVersion) =>
        PgCapabilityDetector.ParseMajorVersion(destinationVersion) >= MinimumServerVersion;

    /// <summary>
    /// Asks for one privilege per parameter, reporting each statement's outcome. A refused
    /// statement is caught and returned, never thrown, cancellation still propagates, and a role
    /// that cannot be read falls back to <c>CURRENT_USER</c> — the pre-flight is best effort, so it
    /// must never be the reason a stage fails.
    /// </summary>
    public static async Task<IReadOnlyList<PgSetParameterGrantOutcome>> GrantAsync(
        ISqlExecutor executor,
        IReadOnlyList<string> parameters,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(logger);

        if (parameters.Count == 0)
            return [];

        // The role the copy runs as. CURRENT_USER answers the same thing and is the fallback when
        // the server's answer cannot be read at all.
        var roleSpecification = await ReadRoleSpecificationAsync(executor, logger, cancellationToken);

        var outcomes = new List<PgSetParameterGrantOutcome>(parameters.Count);

        foreach (var parameter in parameters)
        {
            var statement =
                $"GRANT SET ON PARAMETER {PgIdentifierQuoter.QuoteIdentifier(parameter)} TO {roleSpecification}";

            try
            {
                await executor.ExecuteNonQueryAsync(statement, cancellationToken);
                outcomes.Add(new PgSetParameterGrantOutcome(parameter, true, statement));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(
                    new PgSetParameterGrantOutcome(
                        parameter,
                        false,
                        statement,
                        PgExceptionHelper.GetUserMessage(ex)));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// The statement to hand an administrator when a failed object is failed <em>because</em> of a
    /// parameter this run could not grant, or null when the loss is unrelated — in which case the
    /// server's message is the whole answer. One owner for the sentence both the creating stage and
    /// the retrying stage print, so the wording cannot drift between them.
    /// </summary>
    public static string DescribeFailure(
        Exception exception,
        IReadOnlyList<string>? declaredParameters,
        IReadOnlyDictionary<string, string>? refusedStatements)
    {
        var message = PgExceptionHelper.GetUserMessage(exception);
        var statements = AdministratorStatementsFor(exception, declaredParameters, refusedStatements);

        return statements.Count == 0
                   ? message
                   : $"{message} — run on the destination: {string.Join("; ", statements)}";
    }

    /// <summary>
    /// The statements to hand an administrator for a failed object, one per declared parameter this
    /// run failed to grant, in the order the function declares them. Only an insufficient-privilege
    /// answer counts, so a failure with any other cause is never given advice that would not help —
    /// which is also why the test is on the SQLSTATE and not on the message text.
    /// </summary>
    public static IReadOnlyList<string> AdministratorStatementsFor(
        Exception exception,
        IReadOnlyList<string>? declaredParameters,
        IReadOnlyDictionary<string, string>? refusedStatements)
    {
        if (exception is not PostgresException { SqlState: "42501" })
            return [];

        if (declaredParameters is null || refusedStatements is null)
            return [];

        var statements = new List<string>();

        foreach (var parameter in declaredParameters)
        {
            if (refusedStatements.TryGetValue(parameter, out var statement)
                && !statements.Contains(statement, StringComparer.Ordinal))
                statements.Add(statement);
        }

        return statements;
    }

    /// <summary>
    /// The role the copy runs as, quoted. CURRENT_USER is not a departure from the answer — it is
    /// the same role — so a server that cannot answer costs the statement nothing.
    /// </summary>
    private static async Task<string> ReadRoleSpecificationAsync(
        ISqlExecutor executor,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var role = await executor.ExecuteScalarOrNullAsync<string>(
                           "SELECT current_user",
                           cancellationToken);

            return role is null ? "CURRENT_USER" : PgIdentifierQuoter.QuoteIdentifier(role);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The copy's role could not be read; the grant names CURRENT_USER");
            return "CURRENT_USER";
        }
    }

    private static bool IsAllowed(string parameter) =>
        AllowedParameters.Contains(parameter, StringComparer.Ordinal);
}
