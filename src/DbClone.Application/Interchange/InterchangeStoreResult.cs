namespace DbClone.Application.Interchange;

/// <summary>
/// Result of a store (database → catalog) operation.
/// Reports sections, tables, rows, bytes, completeness, verification, and issues.
/// </summary>
/// <remarks>
/// <para>
/// <c>BytesExported</c> is the size of the catalog artifact in bytes — data bytes plus metadata bytes,
/// as the writer measured them. Not the data alone, and not the folder total a file manager reports:
/// that one also counts <c>summary.md</c> and rounds up to cluster size.
/// </para>
/// <para>
/// <c>SummaryPath</c> is the absolute path of the <c>summary.md</c> this store wrote, or
/// <see langword="null"/> when the option was off, the write failed, or the user cancelled while the
/// report was being written. A null here never means the catalog is unusable.
/// </para>
/// </remarks>
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
    TimeSpan Elapsed,
    string? SummaryPath = null);
