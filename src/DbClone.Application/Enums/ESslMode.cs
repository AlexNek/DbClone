namespace DbClone.Application.Enums;

/// <summary>
/// SSL mode for connections.
/// </summary>
public enum ESslMode
{
    /// <summary>No SSL.</summary>
    Disable,

    /// <summary>SSL preferred but not required.</summary>
    Prefer,

    /// <summary>SSL required.</summary>
    Require,

    /// <summary>SSL required and CA certificate verified.</summary>
    VerifyCA,

    /// <summary>SSL required and full certificate chain verified.</summary>
    VerifyFull
}
