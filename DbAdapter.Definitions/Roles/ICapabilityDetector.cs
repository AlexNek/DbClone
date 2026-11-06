using CanonicalModel.DTOs;

namespace DbAdapter.Definitions;

/// <summary>
/// Detects server capabilities.
/// </summary>
public interface ICapabilityDetector
{
    /// <summary>
    /// Detects server capabilities.
    /// </summary>
    Task<ServerCapabilities> DetectAsync(CancellationToken cancellationToken = default);
}
