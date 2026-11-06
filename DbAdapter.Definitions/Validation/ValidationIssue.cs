namespace DbAdapter.Definitions;

/// <summary>
/// A validation issue.
/// </summary>
public sealed record ValidationIssue(
    EValidationSeverity Severity,
    string Message,
    string? ObjectName);
