using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// Result of format detection on a raw connection string.
/// </summary>
public sealed record DetectionResult(
    bool IsDetected,
    EDatabaseProvider Provider,
    string FormatId,
    string FormatDisplayName,
    double Confidence,
    string TypicalSource)
{
    public static DetectionResult None =>
        new(false, default, string.Empty, string.Empty, 0, string.Empty);
}
