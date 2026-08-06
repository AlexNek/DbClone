namespace DbClone.Application.DTOs;

/// <summary>
/// Result of a copy operation.
/// </summary>
public sealed record CopyResult(
    bool Success,
    TimeSpan TotalDuration,
    IReadOnlyList<StageResult> StageResults,
    IReadOnlyList<CopyWarning> Warnings,
    IReadOnlyList<CopyError> Errors,
    CopyStatistics Statistics);
