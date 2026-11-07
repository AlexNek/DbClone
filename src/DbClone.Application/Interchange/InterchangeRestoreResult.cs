namespace DbClone.Application.Interchange;

/// <summary>
/// Result of a restore (catalog → database) operation.
/// Reports applied/skipped objects, the rows written, row-count mismatches, issues, and the validation report.
/// </summary>
public sealed record InterchangeRestoreResult(
    ERestoreOutcome Outcome,
    int ObjectsApplied,
    int ObjectsSkipped,
    long RowsRestored,
    int RowCountMismatches,
    IReadOnlyList<InterchangeIssueView> Issues,
    VerificationSummary? Validation,
    string? LastCompletedChunk,
    TimeSpan Elapsed);
