namespace CanonicalModel.Interchange;

/// <summary>
/// A native SQL definition as captured from the source engine.
/// </summary>
/// <param name="Dialect">The dialect this text is written in (e.g., "postgresql").</param>
/// <param name="Text">The native SQL text.</param>
/// <param name="Complete">Whether this text is the complete definition (true) or a fragment (false).</param>
/// <param name="CapturedBy">Optional identifier of the tool or method that captured this text.</param>
public sealed record NativeDefinition(
    string Dialect,
    string Text,
    bool Complete = true,
    string? CapturedBy = null)
{
    /// <inheritdoc />
    public override string ToString() => Complete ? Text : $"{Text}... (incomplete)";
}
