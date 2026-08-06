using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Validates the destination database against the source by comparing row counts.
/// </summary>
public sealed class ValidateStage : ICopyStage
{
    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.Validate;

    /// <inheritdoc />
    public int Order => 150;

    /// <summary>Initializes a new instance.</summary>
    public ValidateStage(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<ValidateStage>();

        // Validate connections — PgDataCopier may have disposed the originals internally
        var sourceConn = await PgConnectionHelper.ValidateAndReopenAsync(
                             context,
                             isSource: true,
                             logger,
                             cancellationToken);
        var destConn = await PgConnectionHelper.ValidateAndReopenAsync(
                           context,
                           isSource: false,
                           logger,
                           cancellationToken);

        if (sourceConn is null || destConn is null)
        {
            return new StageResult(
                Name,
                false,
                TimeSpan.Zero,
                0,
                    [StageDetail.ConnectionFailed(sourceConn is null ? ECompareSide.Source : ECompareSide.Destination)]);
        }

        var sourceExec = new PgSqlExecutor(
            sourceConn,
            _loggerFactory.CreateLogger<PgSqlExecutor>());
        var destExec = new PgSqlExecutor(destConn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        var model = context.SourceModel!;
        var details = new List<StageDetail>();
        var mismatchCount = 0;
        var matchCount = 0;

        // ── Table data validation (only when CopyData=true) ────────────────────
        if (context.Request.Options.CopyData)
        {
            var verifyMode = context.Request.Options.VerifyMode;
            logger.LogInformation("Validation using VerifyMode={Mode}", verifyMode);

            foreach (var table in model.Tables)
            {
                var qualifiedName =
                    PgIdentifierQuoter.QuoteSchemaQualified(table.SchemaName, table.Name);

                if (context.SkippedTables.Contains(qualifiedName))
                {
                    details.Add(
                        StageDetail.SkippedWarning(
                            qualifiedName, "not created on destination — see warnings"));
                    continue;
                }

                try
                {
                    var result = verifyMode switch
                        {
                            EVerifyMode.RowCount => await CompareRowCountAsync(
                                                        sourceExec,
                                                        destExec,
                                                        qualifiedName,
                                                        cancellationToken),
                            EVerifyMode.Checksum => await CompareChecksumAsync(
                                                        sourceExec,
                                                        destExec,
                                                        qualifiedName,
                                                        cancellationToken),
                            EVerifyMode.Full => await CompareFullAsync(
                                                    sourceExec,
                                                    destExec,
                                                    qualifiedName,
                                                    cancellationToken),
                            _ => new ValidationResult(true, "")
                        };

                    if (result.IsMatch)
                    {
                        matchCount++;
                        details.Add(StageDetail.Matched(qualifiedName, result.Detail));
                    }
                    else
                    {
                        mismatchCount++;
                        details.Add(StageDetail.Mismatched(qualifiedName, result.Detail));
                        context.MismatchedTables.Add(qualifiedName);
                        context.Errors.Add(new CopyError(Name, EStageMessageKind.Mismatch, qualifiedName, null, null));
                        logger.LogError(
                            "Data mismatch for {Table} using {Mode}",
                            qualifiedName,
                            verifyMode);
                    }
                }
                catch (Exception ex)
                {
                    mismatchCount++;
                    details.Add(StageDetail.Exception(PgExceptionHelper.GetUserMessage(ex)));
                    context.MismatchedTables.Add(qualifiedName);
                    context.Errors.Add(new CopyError(Name, EStageMessageKind.Exception, qualifiedName,
                        new Dictionary<string, object> { [PropKeys.Reason] = PgExceptionHelper.GetUserMessage(ex) }, ex));
                    logger.LogError(ex, "Failed to validate {Table}", qualifiedName);
                }
            }
        }

        // ── Object count validation (schema completeness) ──────────────────────
        await ValidateObjectCountsAsync(context, destExec, details, cancellationToken);

        var isValid = mismatchCount == 0;

        if (context.Request.Options.CopyData)
            details.Add(StageDetail.Summary(matchCount, mismatchCount));

        return new StageResult(
            Name,
            isValid,
            TimeSpan.Zero,
            model.Tables.Count,
            details);
    }

    /// <summary>
    /// Verifies that the expected number of schema objects exist on the destination.
    /// Catches silent omissions (e.g. a stage that never ran or an object type not handled).
    /// </summary>
    private static async Task ValidateObjectCountsAsync(
        CopyContext context,
        PgSqlExecutor destExec,
        List<StageDetail> details,
        CancellationToken ct)
    {
        var model = context.SourceModel!;
        var opts = context.Request.Options;
        var sysSchemas = PgSystemSchemas.SqlList;

        // Tables
        var expectedTables = model.Tables.Count - context.SkippedTables.Count;
        var actualTables = (int)await destExec.ExecuteScalarAsync<long>(
            $"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            $"WHERE c.relkind IN ({PgRelKind.TableOrPartition}) AND n.nspname NOT IN ({sysSchemas})", ct);
        if (actualTables != expectedTables)
            details.Add(StageDetail.CountMismatch("Tables", expectedTables, actualTables));

        // Indexes
        var expectedIndexes = opts.CopyIndexes
            ? model.Tables.Sum(t => t.Indexes.Count)
            : model.Tables.Sum(t => t.Indexes.Count(i => i.IsPrimary));
        var actualIndexes = (int)await destExec.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_index i " +
            "JOIN pg_class t ON t.oid = i.indrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            $"WHERE n.nspname NOT IN ({sysSchemas}) " +
            $"AND t.relkind IN ({PgRelKind.TableOrPartition})", ct);
        if (actualIndexes != expectedIndexes)
            details.Add(StageDetail.CountMismatch("Indexes", expectedIndexes, actualIndexes, ELogLevel.Info));

        // Views
        if (opts.CopyViews && model.Views.Count > 0)
        {
            var actual = (int)await destExec.ExecuteScalarAsync<long>(
                $"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                $"WHERE c.relkind = '{PgRelKind.View}' AND n.nspname NOT IN ({sysSchemas})", ct);
            if (actual != model.Views.Count)
                details.Add(StageDetail.CountMismatch("Views", model.Views.Count, actual));
        }

        // Materialized views
        if (opts.CopyMaterializedViews && model.MaterializedViews.Count > 0)
        {
            var actual = (int)await destExec.ExecuteScalarAsync<long>(
                $"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                $"WHERE c.relkind = '{PgRelKind.MaterializedView}' AND n.nspname NOT IN ({sysSchemas})", ct);
            if (actual != model.MaterializedViews.Count)
                details.Add(StageDetail.CountMismatch("Materialized views", model.MaterializedViews.Count, actual));
        }

        // Sequences (exclude identity backing sequences — created implicitly by table DDL)
        if (opts.CopySequences)
        {
            var expectedSeqs = model.Sequences.Count(s => !s.IsIdentity);
            if (expectedSeqs > 0)
            {
                var actual = (int)await destExec.ExecuteScalarAsync<long>(
                    $"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                    $"WHERE c.relkind = '{PgRelKind.Sequence}' AND n.nspname NOT IN ({sysSchemas})", ct);
                // Destination may have extra identity sequences (auto-created by table DDL),
                // so only flag if dest has FEWER than expected.
                if (actual < expectedSeqs)
                    details.Add(StageDetail.CountMismatch("Sequences", expectedSeqs, actual));
            }
        }

        // Functions
        if (opts.CopyFunctions && model.Functions.Count > 0)
        {
            var actual = (int)await destExec.ExecuteScalarAsync<long>(
                $"SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
                $"WHERE n.nspname NOT IN ({sysSchemas})", ct);
            // Functions may include aggregates/window funcs counted differently;
            // only flag if dest has FEWER than expected.
            if (actual < model.Functions.Count)
                details.Add(StageDetail.CountMismatch("Functions", model.Functions.Count, actual));
        }

        // Triggers
        if (opts.CopyTriggers && model.Triggers.Count > 0)
        {
            var actual = (int)await destExec.ExecuteScalarAsync<long>(
                $"SELECT count(*) FROM pg_trigger t " +
                "JOIN pg_class c ON c.oid = t.tgrelid " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                $"WHERE n.nspname NOT IN ({sysSchemas}) AND NOT t.tgisinternal", ct);
            if (actual != model.Triggers.Count)
                details.Add(StageDetail.CountMismatch("Triggers", model.Triggers.Count, actual));
        }
    }

    /// <summary>
    /// Compares data checksums between source and destination using md5 hash of all rows.
    /// </summary>
    private static async Task<ValidationResult> CompareChecksumAsync(
        PgSqlExecutor sourceExec,
        PgSqlExecutor destExec,
        string qualifiedName,
        CancellationToken ct)
    {
        // First check row counts - if different, no need to compute checksum
        var sourceCount = await sourceExec.ExecuteScalarAsync<long>(
                              $"SELECT count(*) FROM {qualifiedName}",
                              ct);
        var destCount = await destExec.ExecuteScalarAsync<long>(
                            $"SELECT count(*) FROM {qualifiedName}",
                            ct);

        if (sourceCount != destCount)
            return new ValidationResult(false, $" (rows: source={sourceCount}, dest={destCount})");

        if (sourceCount == 0)
            return new ValidationResult(true, " (0 rows)");

        // Compare checksums of all rows cast to text
        var sourceChecksum = await sourceExec.ExecuteScalarAsync<string>(
                                 $"SELECT md5(string_agg(t::text, '' ORDER BY t::text)) FROM {qualifiedName} t",
                                 ct);
        var destChecksum = await destExec.ExecuteScalarAsync<string>(
                               $"SELECT md5(string_agg(t::text, '' ORDER BY t::text)) FROM {qualifiedName} t",
                               ct);

        if (string.Equals(sourceChecksum, destChecksum, StringComparison.OrdinalIgnoreCase))
            return new ValidationResult(true, $" ({sourceCount} rows, checksum match)");

        return new ValidationResult(false, $" ({sourceCount} rows, checksum differs)");
    }

    /// <summary>
    /// Full comparison: verifies row counts and data checksums.
    /// Note: For cross-server comparisons, this uses the same checksum approach as Checksum mode.
    /// </summary>
    private static async Task<ValidationResult> CompareFullAsync(
        PgSqlExecutor sourceExec,
        PgSqlExecutor destExec,
        string qualifiedName,
        CancellationToken ct)
    {
        // First check row counts
        var sourceCount = await sourceExec.ExecuteScalarAsync<long>(
                              $"SELECT count(*) FROM {qualifiedName}",
                              ct);
        var destCount = await destExec.ExecuteScalarAsync<long>(
                            $"SELECT count(*) FROM {qualifiedName}",
                            ct);

        if (sourceCount != destCount)
            return new ValidationResult(false, $" (rows: source={sourceCount}, dest={destCount})");

        if (sourceCount == 0)
            return new ValidationResult(true, " (0 rows)");

        // Compare checksums of all rows cast to text (thorough data verification)
        var sourceChecksum = await sourceExec.ExecuteScalarAsync<string>(
                                 $"SELECT md5(string_agg(t::text, '' ORDER BY t::text)) FROM {qualifiedName} t",
                                 ct);
        var destChecksum = await destExec.ExecuteScalarAsync<string>(
                               $"SELECT md5(string_agg(t::text, '' ORDER BY t::text)) FROM {qualifiedName} t",
                               ct);

        if (string.Equals(sourceChecksum, destChecksum, StringComparison.OrdinalIgnoreCase))
            return new ValidationResult(true, $" ({sourceCount} rows, verified)");

        return new ValidationResult(false, $" ({sourceCount} rows, content differs)");
    }

    /// <summary>
    /// Compares row counts between source and destination.
    /// </summary>
    private static async Task<ValidationResult> CompareRowCountAsync(
        PgSqlExecutor sourceExec,
        PgSqlExecutor destExec,
        string qualifiedName,
        CancellationToken ct)
    {
        var sourceRows = await sourceExec.ExecuteScalarAsync<long>(
                             $"SELECT count(*) FROM {qualifiedName}",
                             ct);
        var destRows = await destExec.ExecuteScalarAsync<long>(
                           $"SELECT count(*) FROM {qualifiedName}",
                           ct);

        if (sourceRows == destRows)
            return new ValidationResult(true, $" ({sourceRows} rows)");

        return new ValidationResult(false, $" (source={sourceRows}, dest={destRows})");
    }

    /// <summary>Result of a single table validation.</summary>
    private readonly record struct ValidationResult(bool IsMatch, string Detail);
}
