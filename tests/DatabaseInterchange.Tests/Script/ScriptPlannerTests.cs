using DatabaseInterchange.Script;
using DatabaseInterchange.Tests.Report;

using DbAdapter.Definitions;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Script;

/// <summary>
/// The planner's own contract: the four profiles of D2, the packing of D3, the giant's part count and the
/// promise that the same measurements answer the same plan (D12).
/// </summary>
/// <remarks>
/// The planner reads nothing and writes nothing, so every test here is arithmetic over measures a test can
/// state exactly. The writer's tests are where the files are; this suite is where the decision is.
/// </remarks>
public sealed class ScriptPlannerTests
{
    private const long Budget = 1024;

    [Fact]
    public void Plan_ForACatalogWithNoRows_PlansNoFile()
    {
        // Act
        var plan = ScriptPlanner.Plan(
            [Footprint("empty", rows: 0, estimate: 4096), Footprint("also_empty", rows: 0, estimate: 10)],
            Budget);

        // Assert (D2, FR2) — a capture without rows writes no data file, and the budget is still recorded.
        plan.Profile.Should().Be(EScriptProfile.NoRows);
        plan.Files.Should().BeEmpty();
        plan.Tables.Should().BeEmpty();
        plan.IsFamily.Should().BeFalse();
        plan.BudgetBytes.Should().Be(Budget);
    }

    [Fact]
    public void Plan_AtBudgetZero_PlansTodaysSingleFileWhateverTheSize()
    {
        // Act — 4 GiB of estimates at a budget of zero: the number means "no split", not "impossible".
        var plan = ScriptPlanner.Plan(
            [Footprint("a", rows: 10, estimate: 2048), Footprint("b", rows: 10, estimate: 4_000_000)], 0);

        // Assert (FR13, D6) — one file, every table in load order, and the profile says why.
        plan.Profile.Should().Be(EScriptProfile.Fits);
        plan.BudgetBytes.Should().Be(0);
        plan.Files.Should().ContainSingle().Which.Chunks.Should().HaveCount(2);
        plan.IsFamily.Should().BeFalse();
    }

    [Fact]
    public void Plan_WhenTheWholeCatalogFits_PlansOneFile()
    {
        // Act — three tables, 2 400 estimated bytes between them, against a 4 096 byte budget.
        var plan = ScriptPlanner.Plan(
            [
                Footprint("a", rows: 10, estimate: 800),
                Footprint("b", rows: 10, estimate: 800),
                Footprint("c", rows: 10, estimate: 800),
            ],
            4096);

        // Assert (D2) — one file holds them all, and the largest table is the fact the report states.
        plan.Profile.Should().Be(EScriptProfile.Fits);
        plan.Files.Should().ContainSingle().Which.Estimate.Should().Be(2400);
        plan.LargestTable.Should().Be(new TableRef("public", "a"));
        plan.LargestTableBytes.Should().Be(800);
        plan.IsFamily.Should().BeFalse();
    }

    [Fact]
    public void Plan_WhenEveryTableFitsButTheWholeDoesNot_PacksThemIntoFiles()
    {
        // Act — four 400 byte tables against a 1 000 byte budget: two per file, never one per table.
        var plan = ScriptPlanner.Plan(
            [
                Footprint("a", rows: 10, estimate: 400),
                Footprint("b", rows: 10, estimate: 400),
                Footprint("c", rows: 10, estimate: 400),
                Footprint("d", rows: 10, estimate: 400),
            ],
            1000);

        // Assert (D3, FR4) — a file is filled before the next opens, and the order is the order it arrived in.
        plan.Profile.Should().Be(EScriptProfile.ManySmall);
        plan.IsFamily.Should().BeTrue();
        plan.Files.Should().HaveCount(2);
        plan.Files[0].Chunks.Select(chunk => chunk.Table.Name).Should().Equal("a", "b");
        plan.Files[1].Chunks.Select(chunk => chunk.Table.Name).Should().Equal("c", "d");
        plan.Files[0].Estimate.Should().Be(800);
    }

    [Fact]
    public void Plan_ForAHundredSmallTables_NeverOpensAHundredFiles()
    {
        // Arrange — a hundred tables of about a megabyte each against a ten megabyte budget.
        var footprints = Enumerable.Range(1, 100)
            .Select(index => Footprint($"t{index:000}", rows: 1000, estimate: 1_048_576))
            .ToList();

        // Act
        var plan = ScriptPlanner.Plan(footprints, 10 * 1_048_576);

        // Assert (§6) — the feature is a layout, not a per-table folder: the count is ceil(total / budget).
        plan.Files.Should().HaveCount(10);
        plan.Files.Should().OnlyContain(file => file.Chunks.Count == 10);
        plan.Files.SelectMany(file => file.Chunks).Should().HaveCount(100);
    }

    [Fact]
    public void Plan_KeepsTheLoadOrderAcrossAFileBoundary()
    {
        // Arrange — the order the writer asked for: a parent, then the child whose key points at it.
        var parent = Footprint("parent", rows: 10, estimate: 1000);
        var child = Footprint("child", rows: 10, estimate: 1000);

        // Act
        var plan = ScriptPlanner.Plan([parent, child], 1200);

        // Assert (FR7, D3) — the child lands in the second file, but never ahead of the parent it needs.
        plan.Files.Should().HaveCount(2);
        plan.Files.SelectMany(file => file.Chunks).Select(chunk => chunk.Table.Name)
            .Should().Equal("parent", "child");
    }

    [Fact]
    public void Plan_ForATableLargerThanTheBudget_CutsItIntoCeilingParts()
    {
        // Arrange — a 4 500 byte table against a 1 000 byte budget: five parts, two parts at the least.
        var table = Footprint("big", rows: 1000, estimate: 4500);

        // Act
        var plan = ScriptPlanner.Plan([table], 1000);

        // Assert (D3, FR5) — the parts are numbered in order and carry the rows they are expected to.
        plan.Profile.Should().Be(EScriptProfile.Giants);
        plan.IsSplit(table.Ref).Should().BeTrue();
        plan.Files.Should().HaveCount(5);
        plan.Files.SelectMany(file => file.Chunks).Select(chunk => chunk.Part)
            .Should().Equal(1, 2, 3, 4, 5);
        plan.Files.SelectMany(file => file.Chunks).Should().OnlyContain(chunk => chunk.Rows == 200);
    }

    [Fact]
    public void Plan_ForATableJustOverTheBudget_StillCutsItInTwo()
    {
        // Act — two bytes over is still two parts, because a part is a block the writer can close.
        var plan = ScriptPlanner.Plan([Footprint("big", rows: 3, estimate: 1002)], 1000);

        // Assert
        plan.Profile.Should().Be(EScriptProfile.Giants);
        plan.Files.Should().HaveCount(2);
        plan.Files[1].Chunks.Single().Part.Should().Be(2);
    }

    [Fact]
    public void Plan_PastTenThousandFiles_KeepsEveryChunkInOrder()
    {
        // Arrange — 10 001 one-byte blocks against a two-byte budget: a file per block, past the 9 999th.
        var footprints = Enumerable.Range(1, 10_001)
            .Select(index => Footprint($"t{index:00000}", rows: 1, estimate: 2))
            .ToList();

        // Act
        var plan = ScriptPlanner.Plan(footprints, 2);

        // Assert (D3, D12) — the plan is still the order it arrived in; the width of a name is the writer's.
        plan.Files.Should().HaveCount(10_001);
        plan.Files[0].Chunks.Single().Table.Name.Should().Be("t00001");
        plan.Files[^1].Chunks.Single().Table.Name.Should().Be("t10001");
        plan.Files.SelectMany(file => file.Chunks).Select(chunk => chunk.Table.Name)
            .Should().Equal(footprints.Select(footprint => footprint.Table.Name));
    }

    [Fact]
    public void Plan_IsTheSameTwiceForTheSameMeasurements()
    {
        // Arrange
        var footprints = new List<TableFootprint>
        {
            Footprint("a", rows: 100, estimate: 900),
            Footprint("b", rows: 900, estimate: 12_000),
            Footprint("c", rows: 10, estimate: 30),
        };

        // Act
        var first = ScriptPlanner.Plan(footprints, 4096);
        var second = ScriptPlanner.Plan(footprints, 4096);

        // Assert (D12) — no clock, no enumeration order, no temporary name: the same answer twice.
        first.Profile.Should().Be(second.Profile);
        first.BudgetBytes.Should().Be(second.BudgetBytes);
        first.Files.Select(file => file.Estimate).Should().Equal(second.Files.Select(file => file.Estimate));
        first.Files.SelectMany(file => file.Chunks).Select(chunk => (chunk.Table.Name, chunk.Part, chunk.Rows))
            .Should().Equal(second.Files.SelectMany(file => file.Chunks)
                .Select(chunk => (chunk.Table.Name, chunk.Part, chunk.Rows)));
    }

    [Fact]
    public void Plan_SkipsTheTablesThatHoldNoRows()
    {
        // Act
        var plan = ScriptPlanner.Plan(
            [Footprint("empty", rows: 0, estimate: 5000), Footprint("held", rows: 5, estimate: 100)],
            4096);

        // Assert — a table the catalog records no rows for is not a block, and not a file's reason to exist.
        plan.Tables.Select(table => table.Name).Should().Equal("held");
        plan.Files.Should().ContainSingle().Which.Chunks.Should().ContainSingle();
        plan.LargestTable.Should().Be(new TableRef("public", "held"));
    }

    [Fact]
    public void Plan_OfASchemaOnlyCapture_RecordsTheBudgetItWasGiven()
    {
        // Act
        var plan = ScriptPlanner.None(204_800);

        // Assert (FR2) — the plan exists even when no data file does, so the report can state the number.
        plan.Profile.Should().Be(EScriptProfile.NoRows);
        plan.BudgetBytes.Should().Be(204_800);
        plan.Files.Should().BeEmpty();
        plan.LargestTable.Should().BeNull();
        plan.IsFamily.Should().BeFalse();
    }

    /// <summary>
    /// One table's measure with an exact estimate: the row bytes are half of it, because the estimate is
    /// <c>rowBytes × 2.0</c> and a test should state the number the planner reasons with (D6).
    /// </summary>
    private static TableFootprint Footprint(string name, long rows, long estimate) =>
        new(ReportModel.KeyedTable("public", name), rows, RowBytes: estimate / 2, PayloadBytes: 0);
}
