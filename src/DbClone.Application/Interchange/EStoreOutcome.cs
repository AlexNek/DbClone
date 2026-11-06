namespace DbClone.Application.Interchange;

/// <summary>Outcome of a store (database → catalog) operation.</summary>
public enum EStoreOutcome
{
    /// <summary>All objects exported and verified successfully.</summary>
    Success,

    /// <summary>Export completed but with warnings (partial objects, credential omissions).</summary>
    CompletedWithIssues,

    /// <summary>The operation was cancelled before completion; the catalog is incomplete.</summary>
    Incomplete,

    /// <summary>The operation failed; no usable catalog was produced.</summary>
    Failed
}
