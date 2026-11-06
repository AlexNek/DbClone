using DbClone.Application.DTOs;
using CanonicalModel.DTOs;

namespace DbClone.Application.Copy;

/// <summary>
/// Orchestrates execution of copy pipeline stages.
/// </summary>
public interface ICopyPipeline
{
    /// <summary>
    /// Executes all registered stages in order.
    /// </summary>
    Task<CopyResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default);
}
