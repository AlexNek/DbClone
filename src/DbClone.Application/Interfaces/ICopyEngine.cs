using DbClone.Application.DTOs;

namespace DbClone.Application.Interfaces;

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
