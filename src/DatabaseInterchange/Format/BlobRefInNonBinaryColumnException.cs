namespace DatabaseInterchange.Format;

/// <summary>
/// Thrown when a <c>@blob:</c> reference is found in a column whose recorded logical type is not
/// <c>Binary</c> (design §6.5, §4).
/// </summary>
/// <remarks>
/// A reference is type-scoped on purpose. The inline token space of <c>Binary</c> (<c>\x…</c>) can never
/// begin with <c>@</c>, so inside that type a reference and a value are unambiguous without any escape
/// machinery. A text column may legitimately hold a user's own sentence that starts <c>@blob:</c>; treating
/// it as an address would replace the user's words with bytes read out of a file, so the field is refused
/// as a format error instead — the design's argument that no escape mechanism is needed holds only because
/// this check runs.
/// </remarks>
internal sealed class BlobRefInNonBinaryColumnException : InterchangeFormatException
{
    public BlobRefInNonBinaryColumnException(string message) : base(message)
    {
    }
}
