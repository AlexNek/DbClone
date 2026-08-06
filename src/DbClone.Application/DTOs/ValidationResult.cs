namespace DbClone.Application.DTOs;

/// <summary>
/// Result of validation.
/// </summary>
public sealed record ValidationResult(
    bool IsValid,
    IReadOnlyList<ValidationIssue> Issues);
