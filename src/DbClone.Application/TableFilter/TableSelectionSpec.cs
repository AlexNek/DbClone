using DbClone.Application.Models;

namespace DbClone.Application.TableFilter;

/// <summary>
/// Describes which tables an operation (copy, compare, backup) should process.
/// Uses the exclusion-set model: only tables the user explicitly unchecked are stored,
/// so tables added to the database after a preset was saved are included automatically.
/// A null spec on <c>CopyOptions</c> means no filtering at all.
/// </summary>
public sealed record TableSelectionSpec(bool IsEnabled, IReadOnlySet<TableId> ExcludedTables)
{
    /// <summary>A spec that processes every table (equivalent to no filtering).</summary>
    public static TableSelectionSpec All { get; } = new(false, new HashSet<TableId>());

    /// <summary>
    /// True when this spec actually restricts the operation: enabled and excluding
    /// at least one table. An enabled spec with an empty exclusion set normalizes
    /// to no filtering.
    /// </summary>
    public bool IsActive => IsEnabled && ExcludedTables.Count > 0;

    /// <summary>Checks whether the given table is excluded by this spec.</summary>
    public bool IsExcluded(TableId table) => ExcludedTables.Contains(table);

    /// <summary>Creates an enabled spec that excludes the given tables.</summary>
    public static TableSelectionSpec Excluding(IEnumerable<TableId> excludedTables) =>
        new(true, new HashSet<TableId>(excludedTables));
}
