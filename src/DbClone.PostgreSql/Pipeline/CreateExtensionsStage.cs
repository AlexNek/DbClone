using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates extensions on the destination (e.g. uuid-ossp, pgcrypto).
/// Must run before tables/functions that depend on extension-provided objects.
/// </summary>
public sealed class CreateExtensionsStage : ICopyStage
{
    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateExtensions;

    /// <inheritdoc />
    public int Order => 55;

    /// <summary>Initializes a new instance.</summary>
    public CreateExtensionsStage(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

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
        if (model.Extensions.Count == 0)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "No extensions")]);

        var logger = _loggerFactory.CreateLogger<CreateExtensionsStage>();
        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        var created = 0;
        var details = new List<StageDetail>();

        foreach (var ext in model.Extensions)
        {
            // Extensions hosted in excluded schemas already exist on the destination
            // (that is why the schema is there) or cannot be created — skip the attempt.
            if (!string.IsNullOrEmpty(ext.SchemaName)
                && context.ExcludedSchemas.Contains(ext.SchemaName))
            {
                details.Add(StageDetail.Skipped(ext.Name, $"pre-existing in '{ext.SchemaName}'"));
                created++;
                continue;
            }

            var schemaClause = string.IsNullOrEmpty(ext.SchemaName)
                                   ? ""
                                   : $" SCHEMA {PgIdentifierQuoter.QuoteIdentifier(ext.SchemaName)}";

            var sql =
                $"CREATE EXTENSION IF NOT EXISTS {PgIdentifierQuoter.QuoteIdentifier(ext.Name)}{schemaClause} CASCADE;";

            try
            {
                await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                created++;
                details.Add(StageDetail.Created(ext.Name));
            }
            catch (Exception ex)
            {
                var pgMsg = PgExceptionHelper.GetUserMessage(ex);
                var userMsg =
                    $"extension \"{ext.Name}\" is installed on the source but is not available on the destination — tables depending on it will be skipped";
                logger.LogWarning(
                    ex,
                    "Failed to create extension {Extension}: {Error}",
                    ext.Name,
                    pgMsg);
                context.SkippedExtensions[ext.Name] = ext.SchemaName;
                context.Warnings.Add(
                    new CopyWarning(Name, EStageMessageKind.Skipped, ext.Name,
                        new Dictionary<string, object> { [PropKeys.Reason] = userMsg }));
                details.Add(StageDetail.SkippedWarning(ext.Name, userMsg));
            }
        }

        // Widen the destination search_path so unqualified references to extension-
        // provided functions (e.g. uuid_generate_v4() from uuid-ossp installed in
        // the "extensions" schema on Supabase) resolve in all subsequent stages.
        // Include schemas from ALL source extensions (not just created ones) because
        // the extension may already exist on the destination in that schema.
        var extensionSchemas = model.Extensions
            .Select(e => e.SchemaName)
            .Where(s => !string.IsNullOrEmpty(s)
                        && !string.Equals(s, "public", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(s, "pg_catalog", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extensionSchemas.Count > 0)
        {
            var searchPath = string.Join(
                ", ",
                extensionSchemas.Select(PgIdentifierQuoter.QuoteIdentifier).Append("public"));
            await executor.ExecuteNonQueryAsync(
                $"SET search_path TO {searchPath}",
                cancellationToken);
            logger.LogInformation("Destination search_path set to {SearchPath}", searchPath);
        }

        // Verify that uuid-ossp functions actually exist. On destinations where the
        // pg_extension entry survived but the functions were dropped, CREATE EXTENSION
        // IF NOT EXISTS is a no-op and uuid_generate_v4() is missing. Force-recreate.
        await EnsureUuidFunctionsAsync(context, executor, logger, cancellationToken);

        // Only fail when no extension could be created at all. Individual
        // unavailable extensions (e.g. supabase_vault on vanilla PostgreSQL)
        // are reported as warnings, not fatal errors.
        var success = created > 0;
        return new StageResult(
            Name,
            success,
            TimeSpan.Zero,
            created,
            details);
    }

    /// <summary>
    /// Checks that uuid_generate_v4() is resolvable under the current search_path.
    /// If not, force-recreates the uuid-ossp extension (handles destinations where
    /// the pg_extension row exists but the schema contents were wiped).
    /// </summary>
    private async Task EnsureUuidFunctionsAsync(
        CopyContext context,
        PgSqlExecutor executor,
        ILogger logger,
        CancellationToken ct)
    {
        var exists = await executor.ExecuteScalarAsync<long>(
                         "SELECT COUNT(*) FROM pg_proc WHERE proname = 'uuid_generate_v4'",
                         ct);

        if (exists > 0)
            return;

        logger.LogWarning(
            "uuid_generate_v4() not found on destination — force-recreating uuid-ossp extension");
        try
        {
            await executor.ExecuteNonQueryAsync("DROP EXTENSION IF EXISTS \"uuid-ossp\"", ct);
            await executor.ExecuteNonQueryAsync("CREATE EXTENSION \"uuid-ossp\"", ct);
            logger.LogInformation("uuid-ossp extension recreated in default schema");
        }
        catch (Exception ex)
        {
            var userMsg = PgExceptionHelper.GetUserMessage(ex);
            logger.LogWarning(
                ex,
                "Could not recreate uuid-ossp: {Error}. Tables with uuid_generate_v4() defaults may fail",
                ex.Message);
            context.Warnings.Add(
                new CopyWarning(
                    Name,
                    EStageMessageKind.Failed,
                    "uuid-ossp",
                    new Dictionary<string, object> { [PropKeys.Reason] = userMsg }));
        }
    }
}
