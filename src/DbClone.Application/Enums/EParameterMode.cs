namespace DbClone.Application.Enums;

/// <summary>
/// Parameter mode for function parameters.
/// </summary>
public enum EParameterMode
{
    /// <summary>Input parameter.</summary>
    In,

    /// <summary>Output parameter.</summary>
    Out,

    /// <summary>Input/output parameter.</summary>
    InOut,

    /// <summary>Variadic parameter.</summary>
    Variadic
}
