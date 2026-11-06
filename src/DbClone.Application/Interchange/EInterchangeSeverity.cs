namespace DbClone.Application.Interchange;

/// <summary>Severity of an interchange issue.</summary>
public enum EInterchangeSeverity
{
    /// <summary>Informational note; the operation succeeded.</summary>
    Information,

    /// <summary>Something was preserved but not fully executable, or data may be incomplete.</summary>
    Warning,

    /// <summary>An object could not be applied, or the operation was blocked.</summary>
    Error
}
