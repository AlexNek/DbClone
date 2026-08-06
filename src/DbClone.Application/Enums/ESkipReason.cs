namespace DbClone.Application.Enums;

/// <summary>
/// Why a table or object was skipped during comparison.
/// </summary>
public enum ESkipReason
{
    /// <summary>The connection lacks SELECT privilege on the schema/table.</summary>
    PermissionDenied,

    /// <summary>The operation timed out before completing.</summary>
    Timeout,

    /// <summary>A network error prevented access.</summary>
    NetworkError
}
