using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Execution;
using DbClone.PostgreSql.Metadata;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Reads metadata from the source database.
/// </summary>
public sealed class ReadMetadataStage : ICopyStage
{
    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.ReadMetadata;

    /// <inheritdoc />
    public int Order => 30;

    /// <summary>Initializes a new instance.</summary>
    public ReadMetadataStage(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        var sourceConn = (NpgsqlConnection)context.SourceConnection!;
        var executor = new PgSqlExecutor(sourceConn, _loggerFactory.CreateLogger<PgSqlExecutor>());
        var reader = new PgMetadataReader(
            executor,
            _loggerFactory.CreateLogger<PgMetadataReader>());

        context.SourceModel = await reader.ReadDatabaseModelAsync(
                                  excludePlatformSchemas: context.Request.Options
                                      .ExcludePlatformSchemas,
                                  platformResolution: context.SourcePlatformResolution,
                                  cancellationToken: cancellationToken);

        var model = context.SourceModel;
        var indexCount = model.Tables.Sum(t => t.Indexes.Count(i => !i.IsPrimary));
        var constraintCount = model.Tables.Sum(t =>
            t.ForeignKeys.Count + t.CheckConstraints.Count + t.UniqueConstraints.Count);

        return new StageResult(
            Name,
            true,
            TimeSpan.Zero,
            model.Tables.Count + model.Views.Count + model.Functions.Count,
                [
                    StageDetail.Count(EDatabaseObjectType.Table, model.Tables.Count),
                    StageDetail.Count(EDatabaseObjectType.View, model.Views.Count + model.MaterializedViews.Count),
                    StageDetail.Count(EDatabaseObjectType.Sequence, model.Sequences.Count),
                    StageDetail.Count(EDatabaseObjectType.Function, model.Functions.Count),
                    StageDetail.Count(EDatabaseObjectType.Trigger, model.Triggers.Count),
                    StageDetail.Count(EDatabaseObjectType.Enum, model.Enums.Count),
                    StageDetail.Count(EDatabaseObjectType.Domain, model.Domains.Count),
                    StageDetail.Count(EDatabaseObjectType.CompositeType, model.CompositeTypes.Count),
                    StageDetail.Count(EDatabaseObjectType.Index, indexCount),
                    StageDetail.Count(EDatabaseObjectType.Constraint, constraintCount)
                ]);
    }
}
