namespace DbClone.Application.Interchange;

/// <summary>
/// Result of a restore (catalog → database) operation.
/// Reports applied/skipped objects, row-count mismatches, issues, and the validation report.
/// </summary>
public sealed record InterchangeRestoreResult(
    ERestoreOutcome Outcome,
    int ObjectsApplied,
    int ObjectsSkipped,
    int RowCountMismatches,
    IReadOnlyList<InterchangeIssueView> Issues,
    VerificationSummary? Validation,
    string? LastCompletedChunk,
    TimeSpan Elapsed);
