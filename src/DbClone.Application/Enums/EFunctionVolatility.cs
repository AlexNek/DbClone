namespace DbClone.Application.Enums;

/// <summary>
/// Function volatility category.
/// </summary>
public enum EFunctionVolatility
{
    /// <summary>Always returns the same result for the same arguments.</summary>
    Immutable,

    /// <summary>Result may change within a single table scan.</summary>
    Stable,

    /// <summary>Result can change at any time.</summary>
    Volatile
}
