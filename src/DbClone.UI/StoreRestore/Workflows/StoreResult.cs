namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Result of the store command.
/// </summary>
public sealed record StoreResult(
    bool Success,
    string Message,
    string? CatalogPath,
    int TablesExported,
    long RowsExported,
    double ElapsedSeconds,
    bool HasIssues,
    bool VerificationPassed);
