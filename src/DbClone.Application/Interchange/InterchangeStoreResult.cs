namespace DbClone.Application.Interchange;

/// <summary>
/// Result of a store (database → catalog) operation.
/// Reports sections, tables, rows, bytes, completeness, verification, and issues.
/// </summary>
public sealed record InterchangeStoreResult(
    EStoreOutcome Outcome,
    int SectionsWritten,
    int TablesExported,
    long RowsExported,
    long BytesExported,
    int ChunksWritten,
    bool IsComplete,
    string ConsistencyModeUsed,
    IReadOnlyList<string> TablesThatFellBackToSourceOrder,
    bool VerificationPassed,
    IReadOnlyList<InterchangeIssueView> Issues,
    string? CatalogPath,
    string? PackedZipPath,
    TimeSpan Elapsed);
