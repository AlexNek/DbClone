using DbClone.Application.Copy;

using Microsoft.Extensions.Logging;

using CanonicalModel.Enums;
using CanonicalModel.DTOs;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// Reports the counts of the source model the request carries.
/// Reads no catalog: the model has already been filtered by the application's table
/// selection, and a second read would discard that selection.
/// </summary>
public sealed class ReadMetadataStage : ICopyStage
{
    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.ReadMetadata;

    /// <inheritdoc />
    public int Order => 30;

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// The logger factory is kept as a dependency although this stage no longer reads
    /// the catalog, so the stage list and its registrations stay unchanged.
    /// </remarks>
    public ReadMetadataStage(ILoggerFactory loggerFactory) => _ = loggerFactory;

    /// <inheritdoc />
    public Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        var model = context.SourceModel
            ?? throw new InvalidOperationException("Source model is not available.");

        var indexCount = model.Tables.Sum(t => t.Indexes.Count(i => !i.IsPrimary));
        var constraintCount = model.Tables.Sum(t =>
            t.ForeignKeys.Count + t.CheckConstraints.Count + t.UniqueConstraints.Count);

        return Task.FromResult(
            new StageResult(
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
                    ]));
    }
}
