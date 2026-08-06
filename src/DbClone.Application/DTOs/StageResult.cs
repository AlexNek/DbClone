using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// Result of a single pipeline stage.
/// </summary>
public sealed record StageResult(
    ECopyStage StageName,
    bool Success,
    TimeSpan Duration,
    int ObjectsProcessed,
    IReadOnlyList<StageDetail> Details);
