using CanonicalModel.Interchange;

using DatabaseInterchange.Script;
using DatabaseInterchange.Tests.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Script;

/// <summary>
/// The order the table blocks of <c>data.sql</c> are written in: every table a row references comes before the
/// rows that reference it (D6).
/// </summary>
/// <remarks>
/// A key is enforced on every insert the moment the constraint exists, so a block whose parent has not been
/// loaded fails on the server. What is asserted here is the order alone; the file's layout is
/// <see cref="CatalogScriptWriterTests"/>' subject.
/// </remarks>
public class TableLoadOrderTests
{
    [Fact]
    public void Order_PutsAParentBeforeAChild_WhoseNameSortsFirst()
    {
        // Arrange — the catalog order is the alphabetical one the sections are written in, which is wrong
        // for a load.
        var order = TableLoadOrder.Order([Child("a_orders", "z_customers"), Parent("z_customers")]);

        order.Select(table => table.Name).Should().Equal("z_customers", "a_orders");
    }

    [Fact]
    public void Order_FollowsAChainOfKeys_DeepestParentFirst()
    {
        // Arrange — c points at b, b points at a, and the catalog sorts them the other way round.
        var order = TableLoadOrder.Order(
        [
            Child("c_lines", "b_orders"),
            Child("b_orders", "a_customers"),
            Parent("a_customers"),
        ]);

        order.Select(table => table.Name).Should().Equal("a_customers", "b_orders", "c_lines");
    }

    [Fact]
    public void Order_KeepsTheCatalogOrder_AmongTablesNoKeyJoins()
    {
        // Arrange — nothing points at anything, so no table has to wait and the recorded order is the answer.
        var order = TableLoadOrder.Order([Parent("b_second"), Parent("a_first")]);

        order.Select(table => table.Name).Should().Equal("b_second", "a_first");
    }

    [Fact]
    public void Order_KeepsTheCatalogOrder_ForTheMembersOfACycle()
    {
        // Arrange — a cycle has no loadable order at all: each block is its own transaction. Rather than
        // guess, the members keep the order the catalog recorded, which is the order the note names them in.
        var order = TableLoadOrder.Order(
        [
            Child("a_orders", "b_customers"),
            Child("b_customers", "a_orders"),
            Parent("c_audit"),
        ]);

        order.Select(table => table.Name).Should().Equal("c_audit", "a_orders", "b_customers");
    }

    [Fact]
    public void Order_IgnoresAReferenceToATableTheCatalogDidNotCapture()
    {
        // Arrange — the parent lives outside this store, so no block of this file can wait for it.
        var order = TableLoadOrder.Order(
        [
            ReportModel.KeyedTable("public", "a_orders") with
            {
                ForeignKeys =
                [
                    ReportModel.UnresolvedReference(
                        "public", "a_orders", "fk_ext", ObjectRef.Table("other", "z_ext"), "id"),
                ],
            },
            Parent("z_customers"),
        ]);

        order.Select(table => table.Name).Should().Equal("a_orders", "z_customers");
    }

    [Fact]
    public void Order_IgnoresASelfReference()
    {
        // Arrange — a row pointing at another row of its own table says nothing about the order of blocks.
        var order = TableLoadOrder.Order(
        [
            ReportModel.KeyedTable("public", "employees") with
            {
                ForeignKeys = [Reference("employees", "employees")],
            },
            Parent("z_customers"),
        ]);

        order.Select(table => table.Name).Should().Equal("employees", "z_customers");
    }

    [Fact]
    public void Order_AnswersTheSameTables_ForACatalogOfOne()
    {
        var tables = new[] { Parent("only") };

        TableLoadOrder.Order(tables).Should().Equal(tables);
    }

    private static TableDefinition Parent(string name) => ReportModel.KeyedTable("public", name);

    private static TableDefinition Child(string name, string parent) =>
        ReportModel.KeyedTable("public", name) with
        {
            ForeignKeys = [Reference(name, parent)],
        };

    private static ForeignKeyDefinition Reference(string child, string parent) =>
        ReportModel.Reference("public", child, $"fk_{child}_{parent}", "public", parent, ["id"], ["id"]);
}
