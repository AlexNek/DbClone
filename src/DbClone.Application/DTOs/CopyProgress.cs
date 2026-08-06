using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// High-level progress information for the copy operation.
/// </summary>
public sealed record CopyProgress(
    ECopyStage CurrentStage,
    int CompletedStages,
    int TotalStages,
    double PercentComplete,
    double ElapsedSeconds,
    StageResult? CompletedStage = null,
    TableProgress? TableProgress = null);
