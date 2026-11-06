using CanonicalModel.DTOs;

namespace DbAdapter.Definitions;

/// <summary>
/// Orchestrates the full database copy operation.
/// </summary>
public interface ICopyEngine
{
    /// <summary>
    /// Executes a full database copy operation.
    /// </summary>
    Task<CopyResult> ExecuteCopyAsync(
        CopyRequest request,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
