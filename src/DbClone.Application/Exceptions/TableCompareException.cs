using DbClone.Application.Enums;

namespace DbClone.Application.Exceptions;

/// <summary>
/// Thrown when a table comparison fails on one side of a two-database comparison.
/// Carries the failing side and the SQLSTATE code as structured fields so callers
/// never need to parse the message string. <see cref="Exception.Message"/> holds the
/// clean server message (no SQLSTATE prefix, no side prefix).
/// </summary>
public sealed class TableCompareException : Exception
{
    /// <summary>Which side of the comparison the failure occurred on.</summary>
    public ECompareSide Side { get; }

    /// <summary>SQLSTATE error code (e.g. "42501" for insufficient_privilege), if available.</summary>
    public string? SqlState { get; }

    public TableCompareException(
        ECompareSide side,
        string message,
        string? sqlState = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Side = side;
        SqlState = sqlState;
    }
}
