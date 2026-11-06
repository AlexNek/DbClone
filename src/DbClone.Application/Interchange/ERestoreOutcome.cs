namespace DbClone.Application.Interchange;

/// <summary>Outcome of a restore (catalog → database) operation.</summary>
public enum ERestoreOutcome
{
    /// <summary>All objects applied successfully and validated.</summary>
    Success,

    /// <summary>Restore completed but with warnings (partial catalog, non-deployable routines).</summary>
    CompletedWithIssues,

    /// <summary>The operation was cancelled; some objects may have been applied.</summary>
    Cancelled,

    /// <summary>The restore was refused before any DDL ran (non-empty target, bad version, corrupt catalog).</summary>
    Refused,

    /// <summary>The operation failed after applying some objects.</summary>
    Failed
}
