using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Synchronizes sequence values on the destination.
/// </summary>
public sealed class SyncSequencesStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.SyncSequences;

    /// <inheritdoc />
    public int Order => 110;

    /// <summary>Initializes a new instance.</summary>
    public SyncSequencesStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
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

        var model = context.SourceModel!;
        var sourceConn = (NpgsqlConnection)context.SourceConnection!;
        var destConn = (NpgsqlConnection)context.DestinationConnection!;

        var sourceExec = new PgSqlExecutor(
            sourceConn,
            _loggerFactory.CreateLogger<PgSqlExecutor>());
        var destExec = new PgSqlExecutor(destConn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        var synced = 0;
        var skipped = 0;
        var failed = 0;
        var details = new List<StageDetail>();
        foreach (var seq in model.Sequences)
        {
            var sourceSeqName = $"{seq.SchemaName}.{seq.Name}";
            try
            {
                var currentVal = await sourceExec.ExecuteScalarAsync<long>(
                                     $"SELECT last_value FROM {PgIdentifierQuoter.QuoteSchemaQualified(seq.SchemaName, seq.Name)}",
                                     cancellationToken);

                // Owned sequences (identity/serial) may have a different name on the
                // destination. Resolve the actual name via pg_get_serial_sequence().
                string destSchema, destName;
                if (seq.IsOwned && seq.OwnerColumn is not null)
                {
                    string? resolved;
                    try
                    {
                        resolved = await destExec.ExecuteScalarAsync<string>(
                                       $"SELECT pg_get_serial_sequence('{seq.OwnerTable}', '{seq.OwnerColumn}')",
                                       cancellationToken);
                    }
                    catch (InvalidOperationException)
                    {
                        // pg_get_serial_sequence returned NULL — column has no sequence on dest
                        resolved = null;
                    }

                    if (resolved is null)
                    {
                        skipped++;
                        var reason =
                            $"Owning column {seq.OwnerTable}.{seq.OwnerColumn} has no sequence on destination";
                        details.Add(StageDetail.SkippedWarning(sourceSeqName, reason));
                        context.Warnings.Add(
                            new CopyWarning(
                                Name,
                                EStageMessageKind.Skipped,
                                sourceSeqName,
                                new Dictionary<string, object>
                                {
                                    [PropKeys.Reason] = reason
                                }));
                        continue;
                    }

                    // pg_get_serial_sequence returns schema-qualified, possibly quoted
                    var (rs, rn) = ParseQualifiedSequence(resolved);
                    destSchema = rs;
                    destName = rn;
                }
                else
                {
                    destSchema = seq.SchemaName;
                    destName = seq.Name;
                }

                var setValSql = _ddl.GenerateSetSequenceValue(destSchema, destName, currentVal);
                await destExec.ExecuteNonQueryAsync(setValSql, cancellationToken);
                synced++;
            }
            catch (Exception ex)
            {
                failed++;
                var userMsg = PgExceptionHelper.GetUserMessage(ex);
                details.Add(StageDetail.FailedWarning(sourceSeqName, userMsg));
                context.Warnings.Add(
                    new CopyWarning(
                        Name,
                        EStageMessageKind.Failed,
                        sourceSeqName,
                        new Dictionary<string, object> { [PropKeys.Reason] = userMsg }));
            }
        }

        context.Statistics = context.Statistics with { SequencesSynced = synced };

        details.Insert(0, StageDetail.Statistic("Sequences synced", synced));
        if (skipped > 0)
            details.Insert(1, StageDetail.Statistic("Sequences skipped", skipped));
        if (failed > 0)
            details.Insert(skipped > 0 ? 2 : 1, StageDetail.Statistic("Sequences failed", failed));

        return new StageResult(
            Name,
            true,
            TimeSpan.Zero,
            synced + skipped + failed,
            details);
    }

    /// <summary>
    /// Parses a possibly-quoted schema-qualified sequence name returned by
    /// pg_get_serial_sequence (e.g. <c>"public"."users_id_seq"</c> or <c>public.users_id_seq</c>).
    /// </summary>
    private static (string Schema, string Name) ParseQualifiedSequence(string qualified)
    {
        // pg_get_serial_sequence always returns schema-qualified, with quoting as needed
        var parts = qualified.Split('.', 2);
        if (parts.Length != 2)
            return ("public", qualified.Trim('"'));

        return (parts[0].Trim('"'), parts[1].Trim('"'));
    }
}
