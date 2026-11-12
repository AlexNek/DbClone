using DbClone.Application.Interchange;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Result of the restore command.
/// </summary>
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
    string? LastCompletedChunk,
    IReadOnlyList<InterchangeIssueView>? Issues = null);
