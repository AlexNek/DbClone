namespace DbClone.Application.Enums;

/// <summary>
/// Severity level of a pipeline detail or log entry.
/// Assigned by the producer that knows the outcome; consumers (UI filtering,
/// coloring, reports) rely on this value instead of parsing message text.
/// </summary>
public enum ELogLevel
{
    /// <summary>Normal informational message.</summary>
    Info,

    /// <summary>Explanatory hint about behavior or configuration.</summary>
    Hint,

    /// <summary>Non-fatal issue the user should review.</summary>
    Warning,

    /// <summary>Failure or error requiring attention.</summary>
    Error
}
