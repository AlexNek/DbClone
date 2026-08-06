namespace DbClone.UI.Models;

/// <summary>
/// Display wrapper for a connection format in the UI dropdown.
/// </summary>
public sealed record FormatListItem(
    string Id,
    string DisplayName,
    string TypicalSource)
{
    public override string ToString() => DisplayName;
}
