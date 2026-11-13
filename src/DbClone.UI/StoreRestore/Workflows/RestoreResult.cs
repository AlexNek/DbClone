using DbClone.Application.Interchange;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Result of the restore command.
/// </summary>
/// <remarks>
/// <c>TablesChecked</c> is the number of tables the check after the data compared row counts for, so
/// <c>RowCountMismatches</c> can be stated as a share of what was restored rather than as a bare number.
/// </remarks>
public sealed record RestoreResult(
    bool Success,
    string Message,
    int ObjectsApplied,
    long RowsRestored,
    double ElapsedSeconds,
    bool HasIssues,
    bool WasCancelled,
    bool WasRefused,
    int RowCountMismatches,
    int TablesChecked,
    string? LastCompletedChunk,
    IReadOnlyList<InterchangeIssueView>? Issues = null);
