namespace DbClone.Application.Interchange;

/// <summary>
/// Result of a restore (catalog → database) operation.
/// Reports applied/skipped objects, the rows written, row-count mismatches, issues, and the validation report.
/// </summary>
/// <remarks>
/// <c>TablesChecked</c> is the number of tables the check after the data compared row counts for — every
/// table the catalog recorded rows for. It is what makes <c>RowCountMismatches</c> readable: a restore
/// that lost the rows of 13 tables out of 28 is a different loss from 13 out of 13.
/// </remarks>
public sealed record InterchangeRestoreResult(
    ERestoreOutcome Outcome,
    int ObjectsApplied,
    int ObjectsSkipped,
    long RowsRestored,
    int RowCountMismatches,
    int TablesChecked,
    IReadOnlyList<InterchangeIssueView> Issues,
    VerificationSummary? Validation,
    string? LastCompletedChunk,
    TimeSpan Elapsed);
