namespace DbClone.Application.Exceptions;

/// <summary>
/// Thrown when a database connection test fails.
/// Carries the SQLSTATE code as a separate field so callers never need to parse
/// the message string. <see cref="Exception.Message"/> holds the clean server
/// message (no SQLSTATE prefix).
/// </summary>
public sealed class DatabaseConnectionException : Exception
{
    /// <summary>SQLSTATE error code (e.g. "3D000" for invalid_catalog_name), if available.</summary>
    public string? SqlState { get; }

    public DatabaseConnectionException(
        string message,
        string? sqlState = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SqlState = sqlState;
    }
}
