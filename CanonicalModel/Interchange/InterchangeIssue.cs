namespace CanonicalModel.Interchange;

/// <summary>
/// An issue or observation recorded during interchange operations.
/// </summary>
/// <param name="Code">Stable issue code (e.g., "RoutineNotDeployable", "CredentialOmitted").</param>
/// <param name="Severity">Issue severity.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="TargetRef">Optional reference of the affected object.</param>
public sealed record InterchangeIssue(
    string Code,
    EIssueSeverity Severity,
    string Message,
    ObjectRef TargetRef = default,
    string Details = "")
{
    /// <inheritdoc />
    public override string ToString() => $"[{Severity}] {Code}: {Message}";
}

/// <summary>
/// Issue severity levels.
/// </summary>
public enum EIssueSeverity
{
    /// <summary>Informational observation.</summary>
    Info,

    /// <summary>Warning that may affect behavior.</summary>
    Warning,

    /// <summary>Error that blocks operation.</summary>
    Error,
}
