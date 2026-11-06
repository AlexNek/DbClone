namespace DbClone.Application.Exceptions;

/// <summary>
/// Thrown when a connection's engine value cannot be matched to exactly one installed adapter:
/// none is installed, the named engine is unknown, or more than one is installed and the
/// connection names none. Reported, never guessed (refactors/09 P4/P8).
/// </summary>
public sealed class EngineSelectionException : Exception
{
    public EngineSelectionException(string message)
        : base(message)
    {
    }

    public EngineSelectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
