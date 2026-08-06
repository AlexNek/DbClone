using DbClone.Application.DTOs;

namespace DbClone.Application.Interfaces;

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
