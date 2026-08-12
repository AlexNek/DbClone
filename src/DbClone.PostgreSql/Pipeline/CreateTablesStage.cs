using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates tables on the destination (without foreign keys).
/// </summary>
public sealed class CreateTablesStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly IPgExecutorFactory _executorFactory;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateTables;

    /// <inheritdoc />
    public int Order => 70;

    /// <summary>Initializes a new instance.</summary>
    public CreateTablesStage(PgDdlGenerator ddl, IPgExecutorFactory executorFactory, ILoggerFactory loggerFactory)
    {
        _ddl = ddl;
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
        var logger = _loggerFactory.CreateLogger<CreateTablesStage>();
        var statements = _ddl.GenerateCreateTableStatements(model.Tables);

        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = _executorFactory.Create(conn);

        // Create each table individually so one failure (e.g. unsupported type on the
        // destination) does not abort the remaining tables.
        var created = 0;
        var skipped = 0;
        var failed = 0;
        var details = new List<StageDetail>();
        foreach (var (tableName, sql) in statements)
        {
            try
            {
                await executor.ExecuteNonQueryAsync(sql, cancellationToken);
                created++;
                details.Add(StageDetail.Created(tableName));
            }
            catch (Exception ex)
            {
                // Check whether the failure is caused by an unavailable extension
                // (e.g. vault.secrets depends on supabase_vault). Such tables are
                // reported as notifications so the user can decide how to proceed,
                // but they do not fail the overall copy.
                var dotIdx = tableName.IndexOf('.');
                var tableId = dotIdx > 0
                    ? new Application.Models.TableId(tableName[..dotIdx], tableName[(dotIdx + 1)..])
                    : new Application.Models.TableId(string.Empty, tableName);

                var blockedByExtension = FindBlockingExtension(context, tableId);
                if (blockedByExtension is not null)
                {
                    skipped++;
                    context.SkippedTables.Add(tableId);
                    var reason = $"requires unavailable extension '{blockedByExtension}'";
                    logger.LogWarning(
                        ex,
                        "Table {Table} cannot be created: depends on unavailable extension {Extension}",
                        tableName,
                        blockedByExtension);
                    context.Warnings.Add(
                        new CopyWarning(
                            Name,
                            EStageMessageKind.Skipped,
                            tableName,
                            new Dictionary<string, object>
                            {
                                [PropKeys.Extension] = blockedByExtension,
                                [PropKeys.Reason] = reason
                            }));
                    details.Add(StageDetail.SkippedWarning(tableName, reason));
                }
                else
                {
                    failed++;
                    context.SkippedTables.Add(tableId);
                    var userMsg = PgExceptionHelper.GetUserMessage(ex);
                    logger.LogError(
                        ex,
                        "Failed to create table {Table}: {Error}",
                        tableName,
                        ex.Message);
                    // Also emit a warning so it appears in the final summary alongside
                    // extension-related skips — the user needs to know which tables
                    // were not copied and why.
                    context.Warnings.Add(
                        new CopyWarning(Name, EStageMessageKind.Failed, tableName,
                            new Dictionary<string, object> { [PropKeys.Reason] = userMsg }));
                    context.Errors.Add(
                        new CopyError(Name, EStageMessageKind.Failed, tableName,
                            new Dictionary<string, object> { [PropKeys.Reason] = userMsg }, ex));
                    details.Add(StageDetail.Failed(tableName, userMsg));
                }
            }
        }

        var success = failed == 0;
        if (skipped > 0 || failed > 0)
        {
            details.Add(StageDetail.Summary(created, failed));
        }

        return new StageResult(Name, success, TimeSpan.Zero, created, details);
    }

    /// <summary>
    /// Determines whether a failed table belongs to a schema owned by an extension
    /// that could not be created on the destination. Returns the extension name, or null.
    /// </summary>
    private static string? FindBlockingExtension(CopyContext context, Application.Models.TableId tableId)
    {
        foreach (var (extensionName, schemaName) in context.SkippedExtensions)
        {
            if (string.IsNullOrEmpty(schemaName))
                continue;

            if (string.Equals(tableId.Schema, schemaName, StringComparison.OrdinalIgnoreCase))
                return extensionName;
        }

        return null;
    }
}
