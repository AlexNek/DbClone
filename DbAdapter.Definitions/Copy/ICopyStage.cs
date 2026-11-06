using CanonicalModel.Enums;
using CanonicalModel.DTOs;

namespace DbAdapter.Definitions;

/// <summary>
/// Represents a single stage in the copy pipeline.
/// </summary>
public interface ICopyStage
{
    /// <summary>
    /// Gets the name of this stage.
    /// </summary>
    ECopyStage Name { get; }

    /// <summary>
    /// Gets the order of this stage in the pipeline.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Executes this pipeline stage.
    /// </summary>
    Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default);
}
