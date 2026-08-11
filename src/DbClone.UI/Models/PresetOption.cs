namespace DbClone.UI.Models;

/// <summary>
/// Dropdown entry for a table selection preset.
/// Id = null represents the built-in "All Tables" entry.
/// </summary>
public sealed record PresetOption(string? Id, string Name)
{
    /// <summary>The built-in default entry.</summary>
    public static PresetOption AllTables { get; } = new(null, "All Tables");
}
