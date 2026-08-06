namespace DbClone.Application.Enums;

/// <summary>
/// Semantic category of a pipeline stage message.
/// The UI renderer uses this to compose display text; infrastructure stages
/// report facts, never pre-rendered sentences.
/// </summary>
public enum EStageMessageKind
{
    // Object lifecycle
    Created,
    Failed,
    Skipped,
    Deferred,
    Excluded,
    Altered,

    // Validation
    Match,
    Mismatch,
    StillMismatched,
    Fixed,

    // Connection
    ConnectionFailed,

    // Stage-level
    Cancelled,
    Summary,
    Count,
    Statistic,
    VersionInfo,
    Exception,

    // Infrastructure
    InfrastructureStatus
}
