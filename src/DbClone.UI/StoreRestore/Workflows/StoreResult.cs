namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Result of the store command.
/// </summary>
/// <remarks>
/// <c>SummaryPath</c> is the absolute path of the summary file the store wrote, or null when it was
/// not written. <c>SummaryRequested</c> says whether the user asked for one, so the log can tell a
/// turned-off option apart from a failed write.
/// </remarks>
public sealed record StoreResult(
    bool Success,
    string Message,
    string? CatalogPath,
    int TablesExported,
    long RowsExported,
    double ElapsedSeconds,
    bool HasIssues,
    bool VerificationPassed,
    string? SummaryPath = null,
    bool SummaryRequested = false);
