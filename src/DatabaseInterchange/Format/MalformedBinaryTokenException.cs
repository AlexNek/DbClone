namespace DatabaseInterchange.Format;

/// <summary>
/// Thrown when a <c>Binary</c> column holds a field that is none of the three forms the format allows:
/// <c>\N</c>, an <c>\x</c> hex token, or a <c>@blob:</c> reference (design §6.4).
/// </summary>
/// <remarks>
/// A <c>bytea</c> value rendered by the engine always begins with a backslash, so a field that begins
/// with anything else was not produced by this format's writer. Passing it through would put text the
/// source never held into the target, which is the silent-corruption class the version bump (§7) exists
/// to make impossible.
/// </remarks>
internal sealed class MalformedBinaryTokenException : InterchangeFormatException
{
    public MalformedBinaryTokenException(string message) : base(message)
    {
    }
}
