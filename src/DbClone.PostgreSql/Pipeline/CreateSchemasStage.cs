using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.Application.Platforms;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates schemas on the destination database, then probes which schemas the
/// current user can actually write to. Schemas without CREATE privilege (e.g.
/// Supabase-managed schemas like auth, storage, realtime) are excluded from the
/// working model up front so that all downstream stages skip them entirely
/// instead of failing per-object.
/// </summary>
public sealed class CreateSchemasStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateSchemas;

    /// <inheritdoc />
    public int Order => 50;

    /// <summary>Initializes a new instance.</summary>
    public CreateSchemasStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
    {
        _ddl = ddl;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<CreateSchemasStage>();

        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = new PgSqlExecutor(
            conn,
            _loggerFactory.CreateLogger<PgSqlExecutor>(),
            TimeSpan.FromMinutes(5));

        // ─── Detect and restore missing system schemas ───
        // System schemas (from .platform resolution) are expected to always exist.
        // information_schema in particular can be dropped or missing (template0).
        // Runs in EVERY copy mode: Resume/Update skip DDL creation, but missing
        // system schemas must still be detected and repaired.
        var infoSchemaDetails = new List<StageDetail>();
        await EnsureSystemSchemasAsync(
            executor,
            logger,
            context,
            infoSchemaDetails,
            cancellationToken);

        if (context.Request.Options.CopyMode is ECopyMode.Resume or ECopyMode.Update)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopyMode=Resume/Update"), .. infoSchemaDetails]);

        var model = context.SourceModel!;

        // System schemas (IsSystem) are presence-only entries in the model —
        // they must never be created on the destination.
        var schemas = model.Schemas.Where(s => !s.IsSystem).ToList();

        // Only create schemas that do not already exist on the destination.
        var existing = new HashSet<string>(
            await executor.QueryAsync(
                "SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg_%' AND nspname <> 'information_schema'",
                r => r.GetString(0),
                cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        var missing = schemas.Where(s => !existing.Contains(s.Name)).ToList();
        var statements = _ddl.GenerateCreateSchemas(missing);

        foreach (var sql in statements)
        {
            await executor.ExecuteNonQueryAsync(sql, cancellationToken);
        }

        var details = infoSchemaDetails
            .Concat(schemas.Select(s => StageDetail.Created(s.Name)))
            .ToList();

        // Probe which schemas the current user can create objects in.
        var excluded = await ExcludeNonWritableSchemasAsync(
                           context,
                           executor,
                           logger,
                           cancellationToken);
        foreach (var schema in excluded)
            details.Add(
                StageDetail.Excluded(schema, "no CREATE privilege on destination"));

        return new StageResult(Name, true, TimeSpan.Zero, statements.Count, details);
    }

    /// <summary>
    /// Checks CREATE privilege on every schema present in the source model.
    /// Non-writable schemas are added to <see cref="CopyContext.ExcludedSchemas"/>
    /// and all their objects are removed from <see cref="CopyContext.SourceModel"/>.
    /// Returns the list of excluded schema names.
    /// </summary>
    private async Task<List<string>> ExcludeNonWritableSchemasAsync(
        CopyContext context,
        PgSqlExecutor executor,
        ILogger logger,
        CancellationToken ct)
    {
        var model = context.SourceModel!;

        // Single round-trip: get all schemas the user has CREATE privilege on.
        var writable = new HashSet<string>(
            await executor.QueryAsync(
                "SELECT nspname FROM pg_namespace WHERE has_schema_privilege(current_user, oid, 'CREATE')",
                r => r.GetString(0),
                ct),
            StringComparer.OrdinalIgnoreCase);

        var modelSchemas = model.Schemas
            .Where(s => !s.IsSystem)
            .Select(s => s.Name)
            .Where(n => !string.Equals(n, "public", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var excluded = modelSchemas.Where(s => !writable.Contains(s)).ToList();
        if (excluded.Count == 0)
            return excluded;

        var excludedSet = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        foreach (var schema in excluded)
            context.ExcludedSchemas.Add(schema);

        // Count affected objects for a single informative warning per schema.
        foreach (var schema in excluded)
        {
            var tables = model.Tables.Count(t => string.Equals(
                t.SchemaName,
                schema,
                StringComparison.OrdinalIgnoreCase));
            var functions = model.Functions.Count(f => string.Equals(
                f.SchemaName,
                schema,
                StringComparison.OrdinalIgnoreCase));
            var types = model.Enums.Count(e => string.Equals(
                            e.SchemaName,
                            schema,
                            StringComparison.OrdinalIgnoreCase))
                        + model.CompositeTypes.Count(t => string.Equals(
                            t.SchemaName,
                            schema,
                            StringComparison.OrdinalIgnoreCase))
                        + model.Domains.Count(d => string.Equals(
                            d.SchemaName,
                            schema,
                            StringComparison.OrdinalIgnoreCase));

            var parts = new List<string>();
            if (tables > 0) parts.Add($"{tables} tables");
            if (functions > 0) parts.Add($"{functions} functions");
            if (types > 0) parts.Add($"{types} types");
            var summary = parts.Count > 0 ? $" ({string.Join(", ", parts)} skipped)" : "";

            logger.LogWarning(
                "Schema {Schema} excluded: no CREATE privilege on destination{Summary}",
                schema,
                summary);
            context.Warnings.Add(
                new CopyWarning(
                    Name,
                    EStageMessageKind.Excluded,
                    schema,
                    new Dictionary<string, object> { [PropKeys.Reason] = "no CREATE privilege on destination" }));
        }

        // Remove all objects in excluded schemas from the working model so that
        // every downstream stage (types, functions, tables, data, constraints,
        // triggers, validate) skips them automatically.
        context.SourceModel = model.ExcludeSchemas(excludedSet);

        logger.LogInformation(
            "Excluded {Count} non-writable schemas: {Schemas}. Remaining: {Tables} tables, {Functions} functions",
            excluded.Count,
            string.Join(", ", excluded),
            context.SourceModel.Tables.Count,
            context.SourceModel.Functions.Count);

        return excluded;
    }

    /// <summary>
    /// Checks presence of all system schemas from the platform resolution on the
    /// destination database. For <c>information_schema</c> (the only repairable one),
    /// attempts restoration via the server-side install script. Other system schemas
    /// (pg_catalog, pg_toast) cannot practically be dropped — if missing, the database
    /// is fundamentally broken and we surface a critical warning.
    /// </summary>
    private static async Task EnsureSystemSchemasAsync(
        PgSqlExecutor executor,
        ILogger logger,
        CopyContext context,
        List<StageDetail> details,
        CancellationToken ct)
    {
        // Use platform resolution if available, otherwise fall back to hardcoded list
        IReadOnlySet<string> systemSchemas =
            context.DestinationPlatformResolution?.SystemSchemas is { Count: > 0 } resolved
                ? resolved
                : PgSystemSchemas.AllSet;

        // Single round-trip: check which system schemas exist
        var sqlList = string.Join(", ", systemSchemas.Select(s => $"'{s.Replace("'", "''")}'"));
        var existingSchemas = new HashSet<string>(
            await executor.QueryAsync(
                $"SELECT nspname FROM pg_namespace WHERE nspname IN ({sqlList})",
                r => r.GetString(0),
                ct),
            StringComparer.OrdinalIgnoreCase);

        var missing = systemSchemas.Where(s => !existingSchemas.Contains(s)).ToList();
        if (missing.Count == 0)
            return;

        foreach (var schema in missing)
        {
            if (string.Equals(schema, "information_schema", StringComparison.OrdinalIgnoreCase))
            {
                await RepairInformationSchemaAsync(executor, logger, context, details, ct);
            }
            else
            {
                // pg_catalog / pg_toast missing = database is fundamentally broken
                logger.LogCritical(
                    "Destination database is missing system schema {Schema} — database may be unusable",
                    schema);
                details.Add(
                    StageDetail.Infrastructure(
                        $"System schema '{schema}' is missing on destination — database may be unusable"));
                context.Warnings.Add(
                    new CopyWarning(
                        ECopyStage.CreateSchemas,
                        EStageMessageKind.InfrastructureStatus,
                        schema,
                        new Dictionary<string, object>
                        {
                            [PropKeys.Reason] = $"System schema '{schema}' is missing on the destination. "
                                + "This schema cannot be recreated — the database installation may be corrupt."
                        }));
            }
        }
    }

    /// <summary>
    /// Attempts to restore <c>information_schema</c> on the destination. Tries the
    /// server-side install script first, then falls back to creating an empty schema.
    /// </summary>
    private static async Task RepairInformationSchemaAsync(
        PgSqlExecutor executor,
        ILogger logger,
        CopyContext context,
        List<StageDetail> details,
        CancellationToken ct)
    {
        logger.LogWarning(
            "Destination database is missing information_schema — attempting restoration");
        details.Add(
            StageDetail.Infrastructure(
                "information_schema is missing on destination — attempting restoration"));

        string scriptFailure;
        try
        {
            // PostgreSQL stores the information_schema install script in the share
            // directory. We can load and execute it via pg_read_file (requires superuser
            // or pg_read_server_files role).
            var installScript = await TryReadServerFileAsync(executor, ct);

            if (installScript != null)
            {
                await executor.ExecuteNonQueryAsync(installScript, ct);
                logger.LogInformation("Successfully restored information_schema on destination");
                details.Add(
                    StageDetail.Infrastructure(
                        "information_schema restored via server-side install script", ELogLevel.Info));
                return;
            }

            scriptFailure =
                "install script not readable via pg_read_file (requires superuser or pg_read_server_files role)";
        }
        catch (Exception ex)
        {
            scriptFailure = ex.Message;
            logger.LogWarning(
                ex,
                "Could not restore information_schema via server-side script");
        }

        details.Add(
            StageDetail.Infrastructure(
                $"Could not restore information_schema via server-side script — {scriptFailure}"));

        // Fallback: create the bare schema so at least pg_namespace shows it exists,
        // then warn the user that full restoration requires server-side action.
        try
        {
            await executor.ExecuteNonQueryAsync(
                "CREATE SCHEMA IF NOT EXISTS information_schema AUTHORIZATION postgres",
                ct);
            logger.LogWarning(
                "Created empty information_schema schema — views are missing and require server-side restoration");
            details.Add(
                StageDetail.Infrastructure(
                    "Created empty information_schema — views are missing and require server-side restoration"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create information_schema schema");
            details.Add(
                StageDetail.Infrastructure(
                    $"Could not create empty information_schema — {ex.Message}"));
        }

        context.Warnings.Add(
            new CopyWarning(
                ECopyStage.CreateSchemas,
                EStageMessageKind.InfrastructureStatus,
                "information_schema",
                new Dictionary<string, object>
                {
                    [PropKeys.Reason] = "Destination database is missing information_schema. "
                        + "The schema was dropped or the database was created from template0. "
                        + "Other tools (ORMs, pgAdmin, migration frameworks) require it. "
                        + "To fully restore, run on the server: "
                        + "psql -d <dbname> -f $(pg_config --sharedir)/information_schema.sql"
                }));
    }

    /// <summary>
    /// Attempts to read the information_schema install script from standard server paths.
    /// Returns the SQL content if successful, null otherwise.
    /// </summary>
    private static async Task<string?> TryReadServerFileAsync(
        PgSqlExecutor executor,
        CancellationToken ct)
    {
        // Standard paths where PostgreSQL packages install information_schema.sql
        var paths = new[]
                    {
                        // pg_read_file with the share directory (PG 15+: pg_config --sharedir via SQL)
                        @"SELECT pg_read_file(
                              (SELECT setting FROM pg_settings WHERE name = 'data_directory')
                              || '/../share/information_schema.sql')",
                        // Debian/Ubuntu layout
                        @"SELECT pg_read_file('/usr/share/postgresql/'
                              || current_setting('server_version_num')::int / 10000
                              || '/information_schema.sql')",
                        // Generic: use pg_config share path if available
                        // (pg_config view stores names uppercase: 'SHAREDIR')
                        @"SELECT pg_read_file(
                              (SELECT setting FROM pg_config WHERE name = 'SHAREDIR')
                              || '/information_schema.sql')"
                    };

        foreach (var sql in paths)
        {
            try
            {
                var content = await executor.ExecuteScalarAsync<string>(sql, ct);
                if (!string.IsNullOrWhiteSpace(content))
                    return content;
            }
            catch
            {
                // Path doesn't exist or no permission — try next
            }
        }

        return null;
    }
}
