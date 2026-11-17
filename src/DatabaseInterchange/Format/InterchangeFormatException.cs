namespace DatabaseInterchange.Format;

/// <summary>
/// Thrown when a catalog's stored text contradicts the interchange format: a malformed reference, a
/// container that is missing or too small, a name that disagrees with its own bytes.
/// </summary>
/// <remarks>
/// <para>
/// This is the read side's refusal, not a content decision. The format's rules state that a
/// value the catalog cannot account for is corruption and is reported as such — there is no
/// fallback that reads it "as best it can", because a fallback is how a damaged catalog becomes
/// silently wrong data in the target.
/// </para>
/// <para>
/// The named subclasses each carry one rule's message, so a reader can tell a hand-edited container from
/// a truncated copy without parsing the text of the complaint.
/// </para>
/// </remarks>
internal class InterchangeFormatException : Exception
{
    public InterchangeFormatException(string message) : base(message)
    {
    }

    public InterchangeFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
