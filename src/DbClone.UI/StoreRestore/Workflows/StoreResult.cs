using DbClone.Application.Interchange;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Result of the store command.
/// </summary>
/// <remarks>
/// <para>
/// <c>SummaryPath</c> is the absolute path of the summary file the store wrote, or null when it was
/// not written. <c>SummaryRequested</c> says whether the user asked for one, so the log can tell a
/// turned-off option apart from a failed write.
/// </para>
/// <para>
/// <c>SqlScripts</c> answers the same question for the scripts, and <c>SqlScriptsRequested</c> plays the
/// role <c>SummaryRequested</c> plays: without it a user who asked for the files and got none would read
/// the same log as a user who never asked. The paths arrive measured by the product, so the result names
/// the files without the UI knowing what the catalog calls them.
/// </para>
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
    bool SummaryRequested = false,
    SqlScriptPaths? SqlScripts = null,
    bool SqlScriptsRequested = false,
    IReadOnlyList<InterchangeIssueView>? Issues = null);
