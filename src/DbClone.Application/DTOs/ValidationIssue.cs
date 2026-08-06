using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// A validation issue.
/// </summary>
public sealed record ValidationIssue(
    EValidationSeverity Severity,
    string Message,
    string? ObjectName);
