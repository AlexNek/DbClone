using CanonicalModel.DTOs;

namespace DbAdapter.Definitions;

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
