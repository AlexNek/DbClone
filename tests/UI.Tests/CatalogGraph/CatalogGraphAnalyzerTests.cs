using DbClone.Application.Interchange;
using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// The analysis stage's own contract: identity is never text, an edge keeps the constraint's own
/// order, and the opening view comes from measuring neighbourhoods rather than from picking a name.
/// </summary>
public sealed class CatalogGraphAnalyzerTests
{
    private static readonly string Lines = CatalogGraphFixture.TableId("public", "lines");

    private static readonly string Orders = CatalogGraphFixture.TableId("public", "Orders");

    private static readonly string OrdersLower = CatalogGraphFixture.TableId("public", "orders");

    private readonly CatalogGraphAnalyzer _analyzer = new();

    // ── T13: identity, never text ───────────────────────────────────────────────────

    [Fact]
    public void References_that_differ_only_in_case_stay_two_nodes_with_two_counts()
    {
        var view = CatalogGraphFixture.Create()
            .Table("public", "Orders")
            .Table("public", "orders")
            .Table("public", "lines")
            .Edge(Lines, Orders, "fk_lines_upper", ["order_id"])
            .Edge(Lines, OrdersLower, "fk_lines_lower", ["order_id"])
            .Build();

        var model = _analyzer.Analyze(view);

        model.Nodes.Should().HaveCount(3);
        model.NodeIndexesById.Keys.Should().Equal(Orders, Lines, OrdersLower);
        model.NodeIndexesById[Orders].Should().NotBe(model.NodeIndexesById[OrdersLower]);
        model.Nodes[model.NodeIndexesById[Orders]].DegreeIn.Should().Be(1);
        model.Nodes[model.NodeIndexesById[OrdersLower]].DegreeIn.Should().Be(1);
        model.Edges.Select(edge => edge.ParentIndex).Should().BeEquivalentTo(
            [model.NodeIndexesById[Orders], model.NodeIndexesById[OrdersLower]]);
    }

    [Fact]
    public void One_table_name_in_two_schemas_stays_two_nodes()
    {
        var salesCustomers = CatalogGraphFixture.TableId("sales", "customers");
        var dboCustomers = CatalogGraphFixture.TableId("dbo", "customers");

        var view = CatalogGraphFixture.Create()
            .Table("sales", "customers")
            .Table("dbo", "customers")
            .Table("public", "orders")
            .Edge(CatalogGraphFixture.TableId("public", "orders"), salesCustomers, "fk_sales", ["customer_id"])
            .Edge(CatalogGraphFixture.TableId("public", "orders"), dboCustomers, "fk_dbo", ["customer_id"])
            .Build();

        var model = _analyzer.Analyze(view);

        model.NodeIndexesById[salesCustomers].Should().NotBe(model.NodeIndexesById[dboCustomers]);
        model.Nodes[model.NodeIndexesById[salesCustomers]].DisplayName.Should().Be("sales.customers");
        model.Nodes[model.NodeIndexesById[dboCustomers]].DisplayName.Should().Be("dbo.customers");
        model.Edges.Should().HaveCount(2);
    }

    // ── T16: direction and the constraint's own order ──────────────────────────────

    [Fact]
    public void An_edge_points_from_the_table_that_stores_the_value_to_the_one_it_comes_from()
    {
        var view = CatalogGraphFixture.Create()
            .Table("public", "customers")
            .Table("public", "orders")
            .Edge(CatalogGraphFixture.TableId("public", "orders"), CatalogGraphFixture.TableId("public", "customers"),
                "fk_orders_customers", ["customer_id"], ["id"], onDelete: "CASCADE")
            .Build();

        var model = _analyzer.Analyze(view);
        var edge = model.Edges.Should().ContainSingle().Subject;

        model.Nodes[edge.ChildIndex].DisplayName.Should().Be("orders");
        model.Nodes[edge.ParentIndex].DisplayName.Should().Be("customers");
        model.Nodes[edge.ChildIndex].DegreeOut.Should().Be(1);
        model.Nodes[edge.ParentIndex].DegreeIn.Should().Be(1);
        edge.IsMandatory.Should().BeFalse();
        edge.Label.Should().Be("customer_id → customers.id on delete: cascade");
    }

    [Fact]
    public void One_schema_is_not_repeated_on_every_name()
    {
        var view = CatalogGraphFixture.Create()
            .Table("public", "customers")
            .Table("public", "orders")
            .Table("public", "unrelated")
            .Outside("table:?unseen", null, "unseen")
            .Edge(CatalogGraphFixture.TableId("public", "orders"), CatalogGraphFixture.TableId("public", "customers"))
            .Build();

        var model = _analyzer.Analyze(view);

        model.Nodes.Select(node => node.DisplayName).Should().Equal("customers", "orders", "unrelated", "unseen");
        model.Nodes.Select(node => node.Id).Should().Contain(CatalogGraphFixture.TableId("public", "orders"),
            "the prefix is a label decision and identity is untouched");
    }

    [Fact]
    public void The_schema_comes_back_when_two_names_would_otherwise_read_the_same()
    {
        var captured = CatalogGraphFixture.TableId("public", "customers");
        var unresolved = "table:?customers";

        var view = CatalogGraphFixture.Create()
            .Table("public", "customers")
            .Outside(unresolved, null, "customers")
            .Edge(CatalogGraphFixture.TableId("public", "customers"), unresolved, "fk_customers_self")
            .Build();

        var model = _analyzer.Analyze(view);

        model.NodeIndexesById[captured].Should().NotBe(model.NodeIndexesById[unresolved]);
        model.Nodes[model.NodeIndexesById[captured]].DisplayName.Should().Be("public.customers");
        model.Nodes[model.NodeIndexesById[unresolved]].DisplayName.Should().Be("customers",
            "a reference that states no schema has none to print, and the captured node carries the distinction");
    }

    [Fact]
    public void A_composite_constraint_keeps_its_columns_and_its_referenced_columns_index_for_index()
    {
        var view = CatalogGraphFixture.Create()
            .Table("public", "natural_keys")
            .Table("public", "lines")
            .Edge(CatalogGraphFixture.TableId("public", "lines"), CatalogGraphFixture.TableId("public", "natural_keys"),
                "fk_composite", ["tenant_id", "region_id", "code"], ["tenant_id", "region_id", "code"], mandatory: true)
            .Build();

        var edge = _analyzer.Analyze(view).Edges.Should().ContainSingle().Subject;

        edge.Columns.Should().Equal("tenant_id", "region_id", "code");
        edge.ReferencedColumns.Should().Equal("tenant_id", "region_id", "code");
        edge.IsMandatory.Should().BeTrue();
        edge.Label.Should().Be("(tenant_id, region_id, code) → natural_keys.(tenant_id, region_id, code)");
    }

    // ── T21: the seed is measured, never guessed ───────────────────────────────────

    [Fact]
    public void A_dense_catalog_opens_on_a_legible_neighbourhood_and_never_on_the_hub()
    {
        var view = HubAndChainCatalog();
        var model = _analyzer.Analyze(view);

        var hub = model.NodeIndexesById[CatalogGraphFixture.TableId("core", "t000")];

        model.Nodes.Should().HaveCount(150);
        model.Edges.Should().HaveCount(160);
        model.InitialView.ShowsWholeGraph.Should().BeFalse();
        model.InitialView.TableCount.Should().Be(150);
        model.InitialView.FocusNodeIndex.Should().NotBeNull();
        model.InitialView.FocusNodeIndex.Should().NotBe(hub);
        (model.DetailsByNode[hub].NeighbourNodeIndexes.Count + 1).Should().BeGreaterThan(CatalogGraphAnalyzer.InitialFocusNodeLimit);
        (model.DetailsByNode[model.InitialView.FocusNodeIndex!.Value].NeighbourNodeIndexes.Count + 1)
            .Should().BeLessThanOrEqualTo(CatalogGraphAnalyzer.InitialFocusNodeLimit);
        model.InitialView.DenseCandidateCount.Should().Be(1);
    }

    [Fact]
    public void A_table_the_reader_already_selected_seeds_the_view_ahead_of_the_rule()
    {
        var view = HubAndChainCatalog();
        var hubId = CatalogGraphFixture.TableId("core", "t000");

        var model = _analyzer.Analyze(view, hubId);

        var hub = model.NodeIndexesById[hubId];
        model.InitialView.FocusNodeIndex.Should().Be(hub);
        model.InitialView.VisibleNodeIndexes.Should().Contain(hub);
        model.InitialView.VisibleNodeIndexes.Should().HaveCount(model.DetailsByNode[hub].NeighbourNodeIndexes.Count + 1);
        model.Nodes.Should().HaveCount(150);
    }

    [Fact]
    public void A_catalog_with_no_legible_neighbourhood_opens_with_no_focus_and_counts_the_dense_candidates()
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < 70; table++)
        {
            fixture.Table("dense", $"t{table:00}");
        }

        for (var child = 0; child < 70; child++)
        {
            for (var step = 1; step <= 61; step++)
            {
                var parent = (child + step) % 70;
                fixture.Edge(CatalogGraphFixture.TableId("dense", $"t{child:00}"),
                    CatalogGraphFixture.TableId("dense", $"t{parent:00}"), $"fk_{child:00}_{parent:00}");
            }
        }

        var model = _analyzer.Analyze(fixture.Build());

        model.InitialView.FocusNodeIndex.Should().BeNull();
        model.InitialView.VisibleNodeIndexes.Should().BeEmpty();
        model.InitialView.DenseCandidateCount.Should().Be(70);
        model.Nodes.Should().HaveCount(70);
    }

    // ── D8a: the chips and the collapse decision ───────────────────────────────────

    [Fact]
    public void The_chips_are_the_schemas_then_the_cyclic_groups_then_the_unconnected_block()
    {
        var a = CatalogGraphFixture.TableId("public", "a");
        var b = CatalogGraphFixture.TableId("public", "b");

        var view = CatalogGraphFixture.Create()
            .Table("public", "a")
            .Table("public", "b")
            .Table("sales", "lonely")
            .Outside("table:elsewhere.unseen", "elsewhere", "unseen")
            .Edge(a, b, "fk_a_b", ["b_id"])
            .Edge(b, a, "fk_b_a", ["a_id"])
            .Edge(a, "table:elsewhere.unseen", "fk_a_outside", ["outside_id"])
            .Build();

        var model = _analyzer.Analyze(view);

        model.Chips.Select(chip => chip.Kind).Should().Equal(
            EClusterChipKind.Schema,
            EClusterChipKind.Schema,
            EClusterChipKind.CyclicGroup,
            EClusterChipKind.Unconnected);
        model.Chips.Select(chip => chip.Label).Should().Equal("public", "sales", "↺ 2", CatalogGraphAnalyzer.UnconnectedLabel);
        model.ChipIndexesByNode[model.NodeIndexesById[a]].Should().Equal(0, 2);
        model.ChipIndexesByNode[model.NodeIndexesById[b]].Should().Equal(0, 2);
        model.ChipIndexesByNode[model.NodeIndexesById[CatalogGraphFixture.TableId("sales", "lonely")]].Should().Equal(1, 3);
        model.ChipIndexesByNode[model.NodeIndexesById["table:elsewhere.unseen"]].Should().BeEmpty();
    }

    [Theory]
    [InlineData(CatalogGraphAnalyzer.CollapsedComponentSizeLimit, false)]
    [InlineData(CatalogGraphAnalyzer.CollapsedComponentSizeLimit + 1, true)]
    public void A_cyclic_group_collapses_only_above_the_limit(int members, bool collapsed)
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < members; table++)
        {
            fixture.Table("cycled", $"t{table:00}");
        }

        for (var table = 0; table < members; table++)
        {
            var next = (table + 1) % members;
            fixture.Edge(CatalogGraphFixture.TableId("cycled", $"t{table:00}"),
                CatalogGraphFixture.TableId("cycled", $"t{next:00}"), $"fk_{table:00}", ["parent_id"]);
        }

        var model = _analyzer.Analyze(fixture.Build());

        var group = model.CyclicGroups.Should().ContainSingle().Subject;
        group.MemberNodeIndexes.Should().HaveCount(members);
        group.Label.Should().Be($"↺ {members}");
        group.IsCollapsed.Should().Be(collapsed);
        model.Nodes.Should().HaveCount(members);
        model.DetailsByNode.Values.Should().OnlyContain(detail => detail.CyclicGroupNumber == group.Number);
    }

    // ── The fixture the seed rule is measured on ──────────────────────────────────

    /// <summary>
    /// A 150-table, 160-edge catalog whose most referenced table is the hub D8 refuses, because its
    /// closed one-step neighbourhood is wider than the limit.
    /// </summary>
    private static RelationshipGraphView HubAndChainCatalog()
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < 150; table++)
        {
            fixture.Table("core", $"t{table:000}");
        }

        for (var child = 1; child <= 80; child++)
        {
            fixture.Edge(CatalogGraphFixture.TableId("core", $"t{child:000}"),
                CatalogGraphFixture.TableId("core", "t000"), $"fk_hub_{child:000}", ["parent_id"]);
        }

        for (var child = 81; child < 149; child++)
        {
            fixture.Edge(CatalogGraphFixture.TableId("core", $"t{child:000}"),
                CatalogGraphFixture.TableId("core", $"t{child + 1:000}"), $"fk_chain_{child:000}", ["parent_id"]);
        }

        for (var child = 81; child <= 92; child++)
        {
            fixture.Edge(CatalogGraphFixture.TableId("core", $"t{child:000}"),
                CatalogGraphFixture.TableId("core", $"t{child + 30:000}"), $"fk_chord_{child:000}", ["parent_id"]);
        }

        return fixture.Build();
    }
}
