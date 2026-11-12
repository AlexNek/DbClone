using DbAdapter.Definitions;

namespace DatabaseInterchange.Script;

/// <summary>
/// Turns the catalog's per-table measures into the ordered family of files the pass writes: the four profiles
/// of D2, the packing of D3 and the part count of a table larger than the budget.
/// </summary>
/// <remarks>
/// The planner reads nothing and writes nothing: it is a pure function of the measurements, the budget and the
/// order the measurements arrive in — the load order the writer asked for, so a parent's block is planned
/// before its child's whichever file each lands in (FR7). Nothing here touches the clock, the filesystem's
/// enumeration order or a temporary file name (D12).
/// </remarks>
internal static class ScriptPlanner
{
    /// <summary>
    /// Plans the family.
    /// </summary>
    /// <param name="footprints">Every table's measure, in load order.</param>
    /// <param name="budgetBytes">
    /// The largest a data file may become, in bytes; <c>0</c> plans today's single <c>data.sql</c>.
    /// </param>
    /// <returns>The plan the writer obeys.</returns>
    internal static ScriptPlan Plan(IReadOnlyList<TableFootprint> footprints, long budgetBytes)
    {
        ArgumentNullException.ThrowIfNull(footprints);

        var withRows = footprints.Where(footprint => footprint.Rows > 0).ToList();

        if (withRows.Count == 0)
        {
            return None(budgetBytes);
        }

        var largest = withRows.MaxBy(footprint => footprint.Estimate)!;
        var total = withRows.Sum(footprint => footprint.Estimate);
        var tables = withRows.Select(footprint => footprint.Table).ToList();

        if (budgetBytes <= 0 || total <= budgetBytes)
        {
            return new ScriptPlan
            {
                Profile = EScriptProfile.Fits,
                BudgetBytes = Math.Max(0, budgetBytes),
                Files = [Whole(withRows)],
                Tables = tables,
                LargestTable = largest.Ref,
                LargestTableBytes = largest.Estimate,
            };
        }

        var files = new List<ScriptPlanFile>();
        var split = new List<TableRef>();
        ScriptPlanFile? current = null;
        var room = 0L;

        foreach (var footprint in withRows)
        {
            var estimate = footprint.Estimate;

            if (estimate > budgetBytes)
            {
                // A table larger than a whole file is a giant: it opens files of its own, and the writer
                // re-opens its block in each of them as the rows outgrow the one being filled (D3, FR5).
                split.Add(footprint.Ref);

                var parts = (int)Math.Ceiling((double)estimate / budgetBytes);

                var counted = Math.Max(2, parts);
                var partRows = (long)Math.Ceiling((double)footprint.Rows / counted);

                for (var part = 1; part <= counted; part++)
                {
                    var giant = new ScriptPlanFile();
                    giant.Add(new ScriptChunk(footprint.Table, part, partRows), estimate / counted);
                    files.Add(giant);
                }

                current = null;
                room = 0;
                continue;
            }

            if (current is null || estimate > room)
            {
                // The table does not fit the room left, so a new file opens: a hundred small tables land in
                // as few files as the budget allows, and none is moved ahead of a table that precedes it.
                current = new ScriptPlanFile();
                files.Add(current);
                room = budgetBytes;
            }

            current.Add(new ScriptChunk(footprint.Table, 1, footprint.Rows), estimate);
            room -= estimate;
        }

        return new ScriptPlan
        {
            Profile = split.Count > 0 ? EScriptProfile.Giants : EScriptProfile.ManySmall,
            BudgetBytes = budgetBytes,
            Files = files,
            Tables = tables,
            SplitTables = split,
            LargestTable = largest.Ref,
            LargestTableBytes = largest.Estimate,
        };
    }

    /// <summary>
    /// The plan of a catalog that writes no data file at all: the budget is still recorded, so a schema-only
    /// store reports the number it was given (D2, FR2).
    /// </summary>
    /// <param name="budgetBytes">The budget the pass was asked for, in bytes.</param>
    /// <returns>A plan with no file and no table.</returns>
    internal static ScriptPlan None(long budgetBytes) => new()
    {
        Profile = EScriptProfile.NoRows,
        BudgetBytes = Math.Max(0, budgetBytes),
    };

    /// <summary>Plans the single file every table of a catalog that fits goes into.</summary>
    private static ScriptPlanFile Whole(IReadOnlyList<TableFootprint> footprints)
    {
        var file = new ScriptPlanFile();

        foreach (var footprint in footprints)
        {
            file.Add(new ScriptChunk(footprint.Table, 1, footprint.Rows), footprint.Estimate);
        }

        return file;
    }
}
