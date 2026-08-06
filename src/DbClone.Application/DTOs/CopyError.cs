using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// Structured error that occurred during copy.
/// The UI renderer composes the display text from Kind, ObjectName, and Properties.
/// </summary>
public sealed record CopyError(
    ECopyStage StageName,
    EStageMessageKind Kind,
    string? ObjectName,
    IReadOnlyDictionary<string, object>? Properties,
    Exception? Exception);
