using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Creates sequences on the destination.
/// </summary>
public sealed class CreateSequencesStage : ICopyStage
{
    private readonly PgDdlGenerator _ddl;

    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.CreateSequences;

    /// <inheritdoc />
    public int Order => 60;

    /// <summary>Initializes a new instance.</summary>
    public CreateSequencesStage(PgDdlGenerator ddl, ILoggerFactory loggerFactory)
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

        if (!context.Request.Options.CopySequences)
            return new StageResult(
                Name,
                true,
                TimeSpan.Zero,
                0,
                    [StageDetail.Skipped(reason: "CopySequences=false")]);

        var model = context.SourceModel!;

        // Identity sequences (deptype 'i') are created implicitly by the table DDL
        // (GENERATED ... AS IDENTITY). Creating them explicitly would produce orphans
        // with non-matching names on the destination.
        // Serial sequences (deptype 'a') must be created explicitly — the table DDL
        // references them by name via DEFAULT nextval('...').
        var toCreate = model.Sequences.Where(s => !s.IsIdentity).ToList();
        var statements = _ddl.GenerateCreateSequences(toCreate);

        var conn = (NpgsqlConnection)context.DestinationConnection!;
        var executor = new PgSqlExecutor(conn, _loggerFactory.CreateLogger<PgSqlExecutor>());
        var logger = _loggerFactory.CreateLogger<CreateSequencesStage>();

        var created = 0;
        var failed = 0;
        var details = new List<StageDetail>();
        for (var i = 0; i < statements.Count; i++)
        {
            var seq = toCreate[i];
            var seqName = $"{seq.SchemaName}.{seq.Name}";
            try
            {
                await executor.ExecuteNonQueryAsync(statements[i], cancellationToken);
                created++;
                details.Add(StageDetail.Created(seqName));
            }
            catch (Exception ex)
            {
                failed++;
                var userMsg = PgExceptionHelper.GetUserMessage(ex);
                logger.LogError(ex, "Failed to create sequence {Sequence}: {Error}", seqName, ex.Message);
                context.Errors.Add(
                    new CopyError(Name, EStageMessageKind.Failed, seqName,
                        new Dictionary<string, object> { [PropKeys.Reason] = userMsg }, ex));
                details.Add(StageDetail.Failed(seqName, userMsg));
            }
        }

        var success = failed == 0;
        return new StageResult(Name, success, TimeSpan.Zero, created, details);
    }
}
