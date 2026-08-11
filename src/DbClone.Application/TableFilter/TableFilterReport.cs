using DbClone.Application.Models;

namespace DbClone.Application.TableFilter;

/// <summary>
/// A foreign key stripped from an included table because it referenced an excluded table.
/// </summary>
public sealed record DroppedForeignKey(TableId OwningTable, string ConstraintName, TableId ReferencedTable);

/// <summary>
/// Outcome of resolving a <see cref="TableSelectionSpec"/> against a database model.
/// </summary>
/// <param name="FilteredModel">The model with excluded tables and their dependents removed.</param>
/// <param name="Report">Objects removed or adjusted because of the filter (warning material).</param>
public sealed record TableFilterResult(DatabaseModel FilteredModel, TableFilterReport Report);

/// <summary>
/// Structured report of everything a table filter removed beyond the excluded tables
/// themselves: stale preset entries, dangling foreign keys, dependent views and
/// orphaned partitions. Consumers translate it into user-visible warnings.
/// </summary>
public sealed record TableFilterReport(
    IReadOnlyList<TableId> RemovedTables,
    IReadOnlyList<TableId> StaleExclusions,
    IReadOnlyList<DroppedForeignKey> DroppedForeignKeys,
    IReadOnlyList<TableId> SkippedViews,
    IReadOnlyList<TableId> OrphanedPartitions)
{
    /// <summary>An empty report for no-op filtering.</summary>
    public static TableFilterReport Empty { get; } = new([], [], [], [], []);

    /// <summary>True when the filter adjusted anything beyond the requested exclusions.</summary>
    public bool HasWarnings =>
        StaleExclusions.Count > 0
        || DroppedForeignKeys.Count > 0
        || SkippedViews.Count > 0
        || OrphanedPartitions.Count > 0;
}
