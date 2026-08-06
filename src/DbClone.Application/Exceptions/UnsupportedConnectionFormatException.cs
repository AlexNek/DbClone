namespace DbClone.Application.Exceptions;

/// <summary>
/// Thrown when no registered format can parse the provided connection string text.
/// </summary>
public sealed class UnsupportedConnectionFormatException : Exception
{
    public UnsupportedConnectionFormatException()
        : base("No registered connection format could parse the provided text.")
    {
    }

    public UnsupportedConnectionFormatException(string message)
        : base(message)
    {
    }

    public UnsupportedConnectionFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
