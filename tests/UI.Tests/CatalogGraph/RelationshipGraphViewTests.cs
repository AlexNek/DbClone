using System;
using System.Collections.Generic;
using System.Linq;

using DbClone.Application.Relationship;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// T28: the graph a picture is arranged from is the graph it draws. A cluster filter projects the view
/// onto the tables that remain, so the layout never reserves cells for tables nobody will see.
/// </summary>
/// <remarks>
/// The totals of the whole catalog are stated by whoever asked for the reduced view, never by the
/// projection itself — so nothing here is half-derived: either the fact survives with its table or it
/// leaves with it.
/// </remarks>
public sealed class RelationshipGraphViewTests
{
    private static readonly string Imports = CatalogGraphFixture.TableId("staging", "imports");
    private static readonly string Regions = "ext:regions";

    [Fact]
    public void Without_DropsTheHiddenTables_AndEverythingThatOnlyTheyStated()
    {
        // Arrange — two schemas, a relation inside one of them, a table nothing joins, and a constraint
        // whose other end is outside the catalog.
        var view = View();

        // Act
        var shown = view.Without(new HashSet<string>(StringComparer.Ordinal) { Imports });

        // Assert — the table, its edge and the outside end that edge named all leave together.
        shown.Tables.Should().NotContain(table => table.Id == Imports);
        shown.Edges.Should().ContainSingle().Which.ConstraintName.Should().Be("orders_customer_fkey");
        shown.OutsideTables.Should().BeEmpty("nothing left names it");
        shown.UnrelatedTableCount.Should().Be(1, "users is the only table nothing joins");
    }

    [Fact]
    public void Without_RecountsTheSchemas_FromTheTablesItKeeps()
    {
        // Arrange — the staging schema held the one table that is hidden, so its row goes with it.
        var view = View();

        // Act
        var shown = view.Without(new HashSet<string>(StringComparer.Ordinal) { Imports });

        // Assert
        shown.Schemas.Should().BeEquivalentTo(
            [
                new RelationshipSchemaView("auth", 1, 0),
                new RelationshipSchemaView("public", 2, 2),
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void Without_AnEmptySelection_IsTheSameView()
    {
        // Arrange — the chips are all on, so nothing is projected and nothing is recomputed.
        var view = View();

        // Act
        var shown = view.Without(new HashSet<string>(StringComparer.Ordinal));

        // Assert
        shown.Should().BeSameAs(view);
    }

    /// <summary>A catalog of two schemas, one relation, one loner and one reference out of the catalog.</summary>
    private static RelationshipGraphView View() =>
        CatalogGraphFixture.Create()
            .Table("public", "customers", rowCount: 3)
            .Table("public", "orders", rowCount: 5)
            .Table("staging", "imports", rowCount: 1)
            .Table("auth", "users", rowCount: 7)
            .Outside(Regions, "ext", "regions")
            .Edge(
                CatalogGraphFixture.TableId("public", "orders"),
                CatalogGraphFixture.TableId("public", "customers"),
                "orders_customer_fkey",
                ["customer_id"],
                ["id"])
            .Edge(Imports, Regions, "imports_region_fkey", ["region_id"], ["id"])
            .Build();
}
