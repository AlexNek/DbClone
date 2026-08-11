namespace DbClone.Application.Models;

/// <summary>
/// A named table selection preset for one source database.
/// Stores only the tables the user unchecked (exclusion-set model), so tables
/// added later are included automatically. The implicit "All Tables" preset
/// is never stored — it means no exclusions.
/// </summary>
public sealed record TableSelectionPreset(
    string Id,
    string Name,
    IReadOnlySet<TableId> ExcludedTables,
    DateTime CreatedAt,
    DateTime ModifiedAt)
{
    /// <summary>Creates a new preset with a fresh id and current timestamps.</summary>
    public static TableSelectionPreset Create(string name, IEnumerable<TableId> excludedTables)
    {
        var now = DateTime.Now;
        return new TableSelectionPreset(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            ExcludedTables: new HashSet<TableId>(excludedTables),
            CreatedAt: now,
            ModifiedAt: now);
    }
}
