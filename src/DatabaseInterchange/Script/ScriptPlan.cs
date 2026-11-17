using CanonicalModel.Interchange;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Script;

/// <summary>
/// The data family chosen from the catalog's own measures, before the first byte is written: the profile, the
/// budget, the ordered files and the facts the report states about why (D1, D2, FR3).
/// </summary>
/// <remarks>
/// <para>
/// The plan is a pure function of the measurements, the budget and the load order, so the same catalog and the
/// same settings answer the same plan twice (D12). It steers the write but does not bind it: the writer fills
/// a file until the bytes it actually writes no longer fit, so a plan may gain a file, and an S1 plan writes
/// its one <c>data.sql</c> whatever the bytes turn out to be (FR6, D6).
/// </para>
/// <para>
/// The identity of a table is its qualified name, so a plan compares and reads the same way as the catalog it
/// describes.
/// </para>
/// </remarks>
internal sealed class ScriptPlan
{
    /// <summary>Gets the shape this catalog's size distribution produced.</summary>
    internal required EScriptProfile Profile { get; init; }

    /// <summary>Gets the budget the plan was made with, in bytes; <c>0</c> means "one file, whatever the size".</summary>
    internal long BudgetBytes { get; init; }

    /// <summary>Gets the files of the family, in the order they run.</summary>
    internal IReadOnlyList<ScriptPlanFile> Files { get; init; } = [];

    /// <summary>Gets the tables the plan writes, in load order, with the tables that hold no rows left out.</summary>
    internal IReadOnlyList<TableDefinition> Tables { get; init; } = [];

    /// <summary>Gets the tables the plan expects to split, because each is larger than the budget.</summary>
    internal IReadOnlyList<TableRef> SplitTables { get; init; } = [];

    /// <summary>Gets the largest table's qualified name, or <see langword="null"/> when the catalog holds no rows.</summary>
    internal TableRef? LargestTable { get; init; }

    /// <summary>Gets the bytes the largest table is expected to take.</summary>
    internal long LargestTableBytes { get; init; }

    /// <summary>Gets whether the plan produces a numbered family rather than today's single <c>data.sql</c>.</summary>
    internal bool IsFamily => Files.Count > 1;

    /// <summary>Says whether a table is one the plan expects to split into parts.</summary>
    /// <param name="table">The table's qualified name.</param>
    /// <returns><see langword="true"/> when the table is larger than the budget.</returns>
    internal bool IsSplit(TableRef table) => SplitTables.Contains(table);
}
