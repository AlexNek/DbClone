using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// The block alignment's own contract: a run of tables joined one to another is drawn in one column,
/// the table a band stands with is the table it is joined to, no two tables of one band are given the
/// same cell, and the same arrangement aligns the same way on every run.
/// </summary>
public sealed class GraphBlockAlignmentTests
{
    private readonly CatalogGraphAnalyzer _analyzer = new();

    [Fact]
    public void A_run_of_tables_joined_one_to_another_is_drawn_in_one_column()
    {
        // Four tables in a chain, each pointing at the one above it, drawn as the staircase the seed
        // showed: the run is one block, so the alignment has to make it one straight line.
        var model = Chain(4);
        var arrangement = Arrange(model, ["a", "b", "c", "d"], [0, 1, 2, 3], [0, 1, 2, 3]);

        var columns = GraphBlockAlignment.Columns(arrangement, Neighbours(model), down: true, right: true);

        columns.Distinct().Should().HaveCount(1, "a run of tables joined end to end is one straight line");
    }

    [Fact]
    public void A_table_stands_in_the_column_of_the_table_it_is_aligned_with()
    {
        // A parent with three children under it. Reading the bands from the bottom, one child is taken
        // into the parent's block; the push that keeps the band's cells apart must move that child
        // together with its parent, never leave it behind.
        var model = Fan(3);
        var arrangement = Arrange(model, ["parent", "t0", "t1", "t2"], [0, 1, 1, 1], [1, 0, 1, 2]);

        var columns = GraphBlockAlignment.Columns(arrangement, Neighbours(model), down: false, right: false);
        var aligned = arrangement.WithColumns(columns);
        var parent = model.NodeIndexesById[Table("parent")];

        var standing = new[] { "t0", "t1", "t2" }
            .Select(name => model.NodeIndexesById[Table(name)])
            .Where(child => aligned.ColumnOfUnit[child] == aligned.ColumnOfUnit[parent])
            .ToList();

        standing.Should().ContainSingle("exactly one child is aligned with the parent it points at");
    }

    [Fact]
    public void No_two_tables_of_one_band_are_given_the_same_cell()
    {
        var model = Fan(4);
        var arrangement = Arrange(model, ["parent", "t0", "t1", "t2", "t3"], [0, 1, 1, 1, 1], [1, 0, 1, 2, 3]);

        var columns = GraphBlockAlignment.Columns(arrangement, Neighbours(model), down: true, right: false);
        var aligned = arrangement.WithColumns(columns);

        var children = new[] { "t0", "t1", "t2", "t3" }
            .Select(name => aligned.ColumnOfUnit[model.NodeIndexesById[Table(name)]])
            .ToList();

        children.Should().OnlyHaveUniqueItems("two tables drawn in one band may not stand in one cell");
    }

    [Fact]
    public void The_same_arrangement_is_aligned_the_same_way_every_time()
    {
        var model = Fan(4);
        var arrangement = Arrange(model, ["parent", "t0", "t1", "t2", "t3"], [0, 1, 1, 1, 1], [1, 0, 1, 2, 3]);

        var first = GraphBlockAlignment.Columns(arrangement, Neighbours(model), down: true, right: true);
        var second = GraphBlockAlignment.Columns(arrangement, Neighbours(model), down: true, right: true);

        second.Should().Equal(first);
        first.Should().HaveCount(arrangement.UnitCount, "every table is given a column");
    }

    /// <summary>A chain of tables, each pointing at the one above it.</summary>
    /// <param name="length">How many tables the chain holds.</param>
    /// <returns>The analyzed graph.</returns>
    private CatalogGraphModel Chain(int length)
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < length; table++)
        {
            fixture.Table("align", Name(table), 10, Column("parent_id"));
        }

        for (var table = 1; table < length; table++)
        {
            fixture.Edge(Table(Name(table)), Table(Name(table - 1)), $"fk_{table}", ["parent_id"]);
        }

        return _analyzer.Analyze(fixture.Build());
    }

    /// <summary>A parent and the children that all point at it.</summary>
    /// <param name="children">How many children the parent has.</param>
    /// <returns>The analyzed graph.</returns>
    private CatalogGraphModel Fan(int children)
    {
        var fixture = CatalogGraphFixture.Create().Table("align", "parent", 10, Column("id"));
        for (var child = 0; child < children; child++)
        {
            fixture
                .Table("align", $"t{child}", 5, Column("parent_id"))
                .Edge(Table($"t{child}"), Table("parent"), $"fk_t{child}", ["parent_id"]);
        }

        return _analyzer.Analyze(fixture.Build());
    }

    /// <summary>The arrangement a set of cells states, one card per cell.</summary>
    /// <param name="model">The analyzed graph the arrangement is read for.</param>
    /// <param name="tables">The table name of every card, in the order its cell is given.</param>
    /// <param name="rows">The row of every card, in the same order.</param>
    /// <param name="columns">The column of every card, in the same order.</param>
    /// <returns>The arrangement those cells state.</returns>
    private static GraphArrangement Arrange(
        CatalogGraphModel model,
        IReadOnlyList<string> tables,
        IReadOnlyList<int> rows,
        IReadOnlyList<int> columns)
    {
        var units = model.Nodes.Select(node => new List<int> { node.Index }).ToList();
        var shapes = model.Nodes.Select(_ => (Width: 1, Height: 1)).ToList();
        var rowOfUnit = new int[model.Nodes.Count];
        var columnOfUnit = new int[model.Nodes.Count];

        for (var index = 0; index < tables.Count; index++)
        {
            var node = model.NodeIndexesById[Table(tables[index])];
            rowOfUnit[node] = rows[index];
            columnOfUnit[node] = columns[index];
        }

        return new GraphArrangement(model, units, shapes, rowOfUnit, columnOfUnit);
    }

    /// <summary>The tables every table is joined to, each one listed once.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <returns>One list per node.</returns>
    private static List<List<int>> Neighbours(CatalogGraphModel model)
    {
        var neighbours = model.Nodes.Select(_ => new List<int>()).ToList();
        foreach (var edge in model.Edges)
        {
            if (neighbours[edge.ChildIndex].Contains(edge.ParentIndex))
            {
                continue;
            }

            neighbours[edge.ChildIndex].Add(edge.ParentIndex);
            neighbours[edge.ParentIndex].Add(edge.ChildIndex);
        }

        return neighbours;
    }

    private static string Name(int table) => ((char)('a' + table)).ToString();

    private static string Table(string name) => CatalogGraphFixture.TableId("align", name);

    private static RelationshipColumnView Column(string name) =>
        new(name, "integer", false, false, true, false, null);
}
