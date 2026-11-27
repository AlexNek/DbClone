namespace DatabaseInterchange.Format;

/// <summary>
/// Thrown when the format suffix a reference names is not the signature the bytes at its range carry
/// (design §6.8).
/// </summary>
/// <remarks>
/// The writer never renames, re-encodes or repairs content, so a container whose name disagrees with its own
/// bytes was changed after it was written — a truncated copy, an edited file, a moved value. This is the only
/// check that makes the suffix mean something on the read side, and it is why the naming rules forbid a
/// probabilistic entry: a guess would report corruption in a perfectly healthy catalog.
/// </remarks>
internal sealed class BlobFormatMismatchException : InterchangeFormatException
{
    public BlobFormatMismatchException(string message) : base(message)
    {
    }
}
