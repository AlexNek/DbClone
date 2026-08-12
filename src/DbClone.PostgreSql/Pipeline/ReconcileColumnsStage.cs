using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Copy;
using DbClone.Application.Models;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Reconciles column nullability on pre-existing destination tables.
/// When <c>CREATE TABLE IF NOT EXISTS</c> skips a table that already exists,
/// the destination may retain stale column definitions. This stage applies
/// <c>ALTER TABLE ... ALTER COLUMN ... SET NOT NULL</c> for columns that are
/// NOT NULL on the source but nullable on the destination.
/// </summary>
public sealed class ReconcileColumnsStage : ICopyStage
{
    private readonly IPgExecutorFactory _executorFactory;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.ReconcileColumns;

    /// <inheritdoc />
    public int Order => 75;

    /// <summary>Initializes a new instance.</summary>
    public ReconcileColumnsStage(IPgExecutorFactory executorFactory, ILoggerFactory loggerFactory)
    {
        _executorFactory = executorFactory;
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

        if (!context.Request.Options.CopyData)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                [StageDetail.Skipped(reason: "CopyData=false")]);

        var model = context.SourceModel!;
        var logger = _loggerFactory.CreateLogger<ReconcileColumnsStage>();
        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = _executorFactory.Create(conn);

        // Only reconcile regular tables and partitioned parents — partition children
        // inherit nullability from the parent and cannot be altered independently.
        var tables = model.Tables
            .Where(t => t.ParentTable is null)
            .Where(t => !context.SkippedTables.Contains(
                new Application.Models.TableId(t.SchemaName, t.Name)))
            .ToList();

        if (tables.Count == 0)
            return new StageResult(Name, true, TimeSpan.Zero, 0,
                [StageDetail.Skipped(reason: "No tables to reconcile")]);

        var altered = 0;
        var details = new List<StageDetail>();

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Columns the source declares as NOT NULL (excluding identity/generated
            // which are implicitly NOT NULL and cannot be altered).
            var notNullColumns = table.Columns
                .Where(c => !c.IsNullable && !c.IsIdentity && !c.IsGenerated)
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (notNullColumns.Count == 0)
                continue;

            var qualifiedTable = PgIdentifierQuoter.QuoteSchemaQualified(
                table.SchemaName, table.Name);

            // Read which columns are currently NOT NULL on the destination.
            HashSet<string> destNotNull;
            try
            {
                destNotNull = await ReadNotNullColumnsAsync(
                    executor, table.SchemaName, table.Name, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Cannot read column metadata for {Table}", qualifiedTable);
                details.Add(StageDetail.ExceptionWarning(
                    $"Cannot read metadata for {table.SchemaName}.{table.Name}: {ex.Message}"));
                continue;
            }

            // Find columns that need SET NOT NULL.
            var toFix = notNullColumns
                .Where(col => !destNotNull.Contains(col))
                .ToList();

            if (toFix.Count == 0)
                continue;

            foreach (var colName in toFix)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var quotedCol = PgIdentifierQuoter.QuoteIdentifier(colName);
                var sql =
                    $"ALTER TABLE {qualifiedTable} ALTER COLUMN {quotedCol} SET NOT NULL";

                try
                {
                    await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                    altered++;
                    details.Add(StageDetail.Altered(
                        $"{table.SchemaName}.{table.Name}.{colName}", "SET NOT NULL"));
                }
                catch (Exception ex)
                {
                    // SET NOT NULL fails if the column contains NULL values.
                    // Report as warning — the copy can still proceed.
                    var userMsg = PgExceptionHelper.GetUserMessage(ex);
                    logger.LogWarning(
                        ex,
                        "Cannot SET NOT NULL on {Table}.{Column}: {Error}",
                        qualifiedTable, colName, userMsg);
                    context.Warnings.Add(new CopyWarning(
                        Name,
                        EStageMessageKind.Failed,
                        $"{table.SchemaName}.{table.Name}",
                        new Dictionary<string, object> { [PropKeys.Reason] = $"Column {colName} could not be set NOT NULL: {userMsg}" }));
                    details.Add(StageDetail.FailedWarning(
                        $"{table.SchemaName}.{table.Name}.{colName}", userMsg));
                }
            }
        }

        if (altered == 0 && details.Count == 0)
            details.Add(StageDetail.Statistic("Columns already match source nullability", 0));

        return new StageResult(Name, true, TimeSpan.Zero, altered, details);
    }

    /// <summary>
    /// Reads the set of column names that are currently NOT NULL on the destination.
    /// </summary>
    private static async Task<HashSet<string>> ReadNotNullColumnsAsync(
        ISqlExecutor executor,
        string schema,
        string table,
        CancellationToken ct)
    {
        var safeSchema = schema.Replace("'", "''");
        var safeTable = table.Replace("'", "''");
        var sql = $"""
            SELECT a.attname
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = '{safeSchema}'
              AND c.relname = '{safeTable}'
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND a.attnotnull
            """;

        var columns = await executor.QueryAsync(sql, r => r.GetString(0), ct);

        return new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
    }
}
