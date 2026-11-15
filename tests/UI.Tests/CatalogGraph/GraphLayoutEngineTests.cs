using System.Globalization;
using System.Text;

using DbClone.Application.Interchange;
using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// The layout stage's own contract: a card stands in the band its references put it in and under the
/// tables it is joined to — except a table joined to one table only, which is drawn on the free side
/// of it when that table's fan is wider than one corridor can line up — cycles are laid out rather than
/// refused, the three route shapes never share a meaning, no arrow is drawn across a card it does not
/// join, two routes never run along one line, and the same catalog draws the same coordinates every time.
/// </summary>
public sealed class GraphLayoutEngineTests
{
    private readonly CatalogGraphAnalyzer _analyzer = new();

    private readonly GraphLayoutEngine _engine = new();

    /// <summary>The words a fixture states its origin in: every fixture here is a catalog.</summary>
    private static readonly GraphOriginWords Words = GraphOriginWords.For(EGraphOriginKind.Catalog);

    /// <summary>How finely a curve is sampled to read back the ink the document paints for it.</summary>
    private const int InkSamples = 12;

    /// <summary>How finely a route is walked when its ink is compared with another's.</summary>
    private const double InkStep = 2;

    /// <summary>The shortest stretch two routes can share and still be read as two arrows.</summary>
    private const double InkRun = 14;

    // ── T4: one encoding, one meaning ─────────────────────────────────────────────

    [Fact]
    public void A_self_reference_is_one_edge_drawn_as_one_loop()
    {
        var orders = CatalogGraphFixture.TableId("public", "orders");

        var model = Analyze(CatalogGraphFixture.Create()
            .Table("public", "orders", 12, Column("parent_order_id"))
            .Edge(orders, orders, "fk_orders_parent", ["parent_order_id"], ["id"]));

        var edge = model.Edges.Should().ContainSingle().Subject;
        edge.IsSelfReference.Should().BeTrue();
        model.Nodes[edge.ChildIndex].DegreeOut.Should().Be(1);
        model.Nodes[edge.ChildIndex].DegreeIn.Should().Be(1);

        var layout = _engine.Compute(model);

        layout.Edges.Should().ContainSingle().Which.Shape.Should().Be(EEdgeShape.Loop);
        layout.Nodes.Should().ContainSingle();
    }

    [Fact]
    public void An_outside_reference_is_drawn_as_a_line_and_never_as_a_cycle_or_a_loop()
    {
        var orders = CatalogGraphFixture.TableId("public", "orders");
        var accounts = "table:absent.accounts";

        var model = Analyze(CatalogGraphFixture.Create()
            .Table("public", "orders", 12, Column("account_id"))
            .Outside(accounts, "absent", "accounts")
            .Edge(orders, accounts, "fk_orders_accounts", ["account_id"], ["id"]));

        var edge = model.Edges.Should().ContainSingle().Subject;
        edge.ParentIsCaptured.Should().BeFalse();
        model.Nodes[edge.ParentIndex].IsCaptured.Should().BeFalse();
        model.Nodes[edge.ParentIndex].RowCount.Should().BeNull();

        var route = _engine.Compute(model).Edges.Should().ContainSingle().Subject;

        route.Shape.Should().Be(EEdgeShape.Line, "the dash is reserved for an outside table and nothing else");
    }

    [Fact]
    public void A_cycle_is_drawn_as_a_detour_and_neither_throws_nor_drops_a_node()
    {
        var model = Analyze(Cycle("cycled", 3));

        model.CyclicGroups.Should().ContainSingle().Which.MemberNodeIndexes.Should().HaveCount(3);

        var layout = _engine.Compute(model);

        layout.Nodes.Should().HaveCount(3);
        layout.Edges.Should().HaveCount(3);
        layout.Edges.Should().OnlyContain(
            route => route.Shape == EEdgeShape.Detour,
            "a closing cycle is carried by the route, never by the line style an outside table owns");
    }

    [Fact]
    public void A_self_loop_and_an_outside_reference_beside_a_cycle_keep_their_own_shapes()
    {
        var hub = CatalogGraphFixture.TableId("mixed", "hub");
        var left = CatalogGraphFixture.TableId("mixed", "left");
        var right = CatalogGraphFixture.TableId("mixed", "right");
        var outside = "table:absent.elsewhere";

        var model = Analyze(CatalogGraphFixture.Create()
            .Table("mixed", "hub", 5, Column("parent_id"), Column("outside_id"))
            .Table("mixed", "left", 5, Column("hub_id"))
            .Table("mixed", "right", 5, Column("hub_id"))
            .Outside(outside, "absent", "elsewhere")
            .Edge(hub, hub, "fk_hub_parent", ["parent_id"], ["id"])
            .Edge(left, right, "fk_left_right", ["hub_id"], ["id"])
            .Edge(right, left, "fk_right_left", ["hub_id"], ["id"])
            .Edge(hub, outside, "fk_hub_outside", ["outside_id"], ["id"]));

        var layout = _engine.Compute(model);

        layout.Edges.Single(route => model.Edges[route.EdgeIndex].IsSelfReference)
            .Shape.Should().Be(EEdgeShape.Loop);
        layout.Edges.Single(route => !model.Edges[route.EdgeIndex].ParentIsCaptured)
            .Shape.Should().Be(EEdgeShape.Line);
        layout.Edges.Where(route => route.Shape == EEdgeShape.Detour).Should().HaveCount(2);
        layout.Nodes.Should().HaveCount(4);
    }

    // ── T11: a band is a patch, and a group's block holds nobody else ──────────────

    [Fact]
    public void The_members_of_a_cyclic_group_share_one_block_with_nobody_inside_it()
    {
        var fixture = Cycle("blocked", 3).Table("blocked", "loner", 4);
        var model = Analyze(fixture);
        var grid = GridOf(model);
        var layout = _engine.Compute(model);

        var members = model.CyclicGroups.Should().ContainSingle().Subject.MemberNodeIndexes;
        foreach (var member in members)
        {
            foreach (var other in members.Where(node => node != member))
            {
                Step(grid, member, other).Should().BeLessThanOrEqualTo(2, "a cyclic group owns one block of neighbouring cells");
            }
        }

        var boxes = members.Select(node => layout.Nodes[node]).ToArray();
        var left = boxes.Min(box => box.TopLeft.X);
        var top = boxes.Min(box => box.TopLeft.Y);
        var right = boxes.Max(box => box.Right);
        var bottom = boxes.Max(box => box.Bottom);

        var loner = layout.Nodes[model.NodeIndexesById[CatalogGraphFixture.TableId("blocked", "loner")]];
        var inside = loner.TopLeft.X < right && loner.Right > left && loner.TopLeft.Y < bottom && loner.Bottom > top;

        inside.Should().BeFalse("no other table is drawn inside the group's own rectangle");
    }

    [Fact]
    public void A_six_table_component_with_internal_chords_keeps_every_node()
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < 6; table++)
        {
            fixture.Table("chorded", $"t{table}", 10, Column("parent_id"));
        }

        for (var table = 0; table < 6; table++)
        {
            fixture.Edge(Id("chorded", table), Id("chorded", (table + 1) % 6), $"fk_ring_{table}", ["parent_id"]);
        }

        fixture.Edge(Id("chorded", 0), Id("chorded", 3), "fk_chord_0", ["parent_id"]);
        fixture.Edge(Id("chorded", 2), Id("chorded", 5), "fk_chord_2", ["parent_id"]);

        var model = Analyze(fixture);
        var grid = GridOf(model);
        var layout = _engine.Compute(model);

        model.CyclicGroups.Should().ContainSingle().Which.MemberNodeIndexes.Should().HaveCount(6);
        layout.Nodes.Should().HaveCount(6);
        layout.Edges.Should().HaveCount(8);

        foreach (var first in layout.Nodes)
        {
            foreach (var second in layout.Nodes.Where(node => node.NodeIndex != first.NodeIndex))
            {
                Step(grid, first.NodeIndex, second.NodeIndex).Should().BeLessThanOrEqualTo(2,
                    "six tables that reference each other are packed as one patch, never as a strip");
            }
        }
    }

    [Fact]
    public void A_component_fed_by_an_acyclic_upstream_is_drawn_below_it()
    {
        var fixture = CatalogGraphFixture.Create()
            .Table("fed", "root", 10, Column("leaf_id"))
            .Table("fed", "a", 10, Column("root_id"))
            .Table("fed", "b", 10, Column("a_id"))
            .Table("fed", "c", 10, Column("b_id"))
            .Edge(CatalogGraphFixture.TableId("fed", "root"), CatalogGraphFixture.TableId("fed", "a"), "fk_root_a", ["leaf_id"], ["id"])
            .Edge(CatalogGraphFixture.TableId("fed", "a"), CatalogGraphFixture.TableId("fed", "b"), "fk_a_b", ["root_id"], ["id"])
            .Edge(CatalogGraphFixture.TableId("fed", "b"), CatalogGraphFixture.TableId("fed", "c"), "fk_b_c", ["a_id"], ["id"])
            .Edge(CatalogGraphFixture.TableId("fed", "c"), CatalogGraphFixture.TableId("fed", "a"), "fk_c_a", ["b_id"], ["id"]);

        var model = Analyze(fixture);
        var grid = GridOf(model);
        var layout = _engine.Compute(model);

        model.CyclicGroups.Should().ContainSingle().Which.MemberNodeIndexes.Should().HaveCount(3);
        layout.Nodes.Should().HaveCount(4);

        var root = model.NodeIndexesById[CatalogGraphFixture.TableId("fed", "root")];
        var ring = model.CyclicGroups.Should().ContainSingle().Subject.MemberNodeIndexes;

        grid.RowOfNode[root].Should().BeGreaterThan(
            ring.Max(member => grid.RowOfNode[member]),
            "the one table that references the ring is drawn in the band below it");
        grid.ColumnOfNode[root].Should().BeInRange(
            ring.Min(member => grid.ColumnOfNode[member]),
            ring.Max(member => grid.ColumnOfNode[member]),
            "and under the ring, not off to one side of it");
    }

    // ── T5: determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void A_band_under_one_table_is_ordered_by_where_that_table_stands()
    {
        // The catalog behind the reported picture (pass 11): orders carries five tables, three of
        // them in the band under it, and invoices — joined to orders alone — has payments under
        // it. The band kept the order it had been swept in, so invoices was drawn at the far left
        // of the band while orders stood two columns to the right, and the payment hanging off
        // invoices was carried to the opposite corner: a 341 px arrow where 44 px would do, over
        // two arrows that run the other way. The band is now ordered by where its tables stand.
        var addresses = CatalogGraphFixture.TableId("public", "addresses");
        var countries = CatalogGraphFixture.TableId("public", "countries");
        var currencies = CatalogGraphFixture.TableId("public", "currencies");
        var customers = CatalogGraphFixture.TableId("public", "customers");
        var invoices = CatalogGraphFixture.TableId("public", "invoices");
        var lines = CatalogGraphFixture.TableId("public", "order_lines");
        var orders = CatalogGraphFixture.TableId("public", "orders");
        var payments = CatalogGraphFixture.TableId("public", "payments");
        var products = CatalogGraphFixture.TableId("public", "products");
        var shipments = CatalogGraphFixture.TableId("public", "shipments");
        var warehouses = CatalogGraphFixture.TableId("public", "warehouses");

        var fixture = CatalogGraphFixture.Create()
            .Table("public", "addresses", 900, Column("id"))
            .Table("public", "countries", 200, Column("id"))
            .Table("public", "currencies", 12, Column("id"))
            .Table("public", "customers", 40, Column("id"))
            .Table("public", "invoices", 1_100, Column("order_id"))
            .Table("public", "order_lines", 8_000, Column("order_id"), Column("product_id"))
            .Table("public", "orders", 1_200, Column("customer_id"))
            .Table("public", "payments", 1_150, Column("invoice_id"))
            .Table("public", "products", 300, Column("id"))
            .Table("public", "shipments", 700, Column("order_id"))
            .Table("public", "warehouses", 8, Column("id"))
            .Edge(addresses, countries, "fk_addresses_countries", ["id"], ["id"], onDelete: "SET NULL")
            .Edge(invoices, orders, "fk_invoices_orders", ["order_id"], ["id"], mandatory: true)
            .Edge(lines, orders, "fk_lines_orders", ["order_id"], ["id"], mandatory: true, onDelete: "CASCADE")
            .Edge(lines, products, "fk_lines_products", ["product_id"], ["id"], mandatory: true)
            .Edge(lines, products, "fk_lines_products_alt", ["product_id"], ["id"])
            .Edge(orders, currencies, "fk_orders_currency", ["customer_id"], ["id"])
            .Edge(orders, customers, "fk_orders_customers", ["customer_id"], ["id"], mandatory: true, onDelete: "CASCADE")
            .Edge(payments, invoices, "fk_payments_invoices", ["invoice_id"], ["id"], mandatory: true)
            .Edge(shipments, orders, "fk_shipments_orders", ["order_id"], ["id"])
            .Edge(shipments, warehouses, "fk_shipments_warehouse", ["order_id"], ["id"]);

        var model = Analyze(fixture);
        var grid = GridOf(model);

        int Cell(string id) => model.NodeIndexesById[id];

        grid.ColumnOfNode[Cell(invoices)].Should().Be(
            grid.ColumnOfNode[Cell(orders)],
            "invoices is joined to orders alone, so it is drawn under it");
        grid.ColumnOfNode[Cell(payments)].Should().Be(
            grid.ColumnOfNode[Cell(invoices)],
            "payments is joined to invoices alone, so it is drawn under it");

        foreach (var edge in model.Edges)
        {
            Step(grid, edge.ChildIndex, edge.ParentIndex).Should().BeLessThanOrEqualTo(
                1,
                "no arrow of this catalog has to reach further than the next cell: {0}", edge.Label);
        }

        var layout = _engine.Compute(model);
        AssertNoRouteCrossesACard(layout, model);
    }

    [Fact]
    public void The_catalog_behind_the_reported_picture_keeps_every_arrow_clear_of_a_table()
    {
        // The catalog the last picture was reported from (pass 11): two schemas, nine tables, and one
        // band of three template tables beside the two tables the picture opens with. The page's own
        // width for nine cards was two columns, so that band wrapped and the third template table was
        // drawn on a line of its own — between template_entries and the profile it points at, and
        // between alliance_actions and the same profile. Two arrows then had to be carried round a
        // table they do not join. The band needs one column more, so the width is measured against
        // the arrows it draws instead of assumed: the picture is a page, and every arrow has a clear
        // run at what it joins.
        var alliances = CatalogGraphFixture.TableId("public", "alliances");
        var profiles = CatalogGraphFixture.TableId("public", "profiles");
        var actions = CatalogGraphFixture.TableId("public", "alliance_actions");
        var templates = CatalogGraphFixture.TableId("public", "user_templates");
        var stats = CatalogGraphFixture.TableId("public", "template_stats");
        var entries = CatalogGraphFixture.TableId("public", "template_entries");
        var buckets = CatalogGraphFixture.TableId("storage", "buckets_vectors");
        var indexes = CatalogGraphFixture.TableId("storage", "vector_indexes");

        var fixture = CatalogGraphFixture.Create()
            .Table("public", "alliances", 12, Column("id"))
            .Table("public", "profiles", 480, Column("alliance_id"))
            .Table("public", "alliance_actions", 900, Column("alliance_id"), Column("profile_id"))
            .Table("public", "user_templates", 60, Column("profile_id"))
            .Table("public", "template_stats", 40, Column("template_id"))
            .Table("public", "template_entries", 300, Column("profile_id"))
            .Table("public", "users", 200, Column("id"))
            .Table("storage", "buckets_vectors", 3, Column("id"))
            .Table("storage", "vector_indexes", 7, Column("bucket_id"))
            .Edge(profiles, alliances, "fk_profiles_alliances", ["alliance_id"])
            .Edge(actions, alliances, "fk_actions_alliances", ["alliance_id"])
            .Edge(actions, profiles, "fk_actions_profiles", ["profile_id"])
            .Edge(templates, profiles, "fk_templates_profiles", ["profile_id"])
            .Edge(stats, templates, "fk_stats_templates", ["template_id"])
            .Edge(entries, profiles, "fk_entries_profiles", ["profile_id"])
            .Edge(indexes, buckets, "fk_indexes_buckets", ["bucket_id"]);

        var model = Analyze(fixture);
        var grid = GridOf(model);
        var layout = _engine.Compute(model);

        Arrows(grid, model).Measure().Blocked.Should().Be(0, "no arrow is drawn over a table it does not join");
        (grid.Width / grid.Height).Should().BeGreaterThan(
            1.2, "the picture is a page: wider than it is tall");

        AssertNoRouteCrossesACard(layout, model);
    }

    // ── T5: determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void The_same_catalog_lays_out_to_the_same_coordinates_every_time()
    {
        var model = Analyze(Scale(60, 180, 8));

        Signature(_engine.Compute(model)).Should().Be(Signature(_engine.Compute(model)));
    }

    [Fact]
    public void A_shuffled_input_lays_out_to_the_same_coordinates()
    {
        var model = Analyze(DeclaredInOrder(ascending: true));
        var shuffled = Analyze(DeclaredInOrder(ascending: false));

        model.Nodes.Select(node => node.Id).Should().Equal(shuffled.Nodes.Select(node => node.Id));
        model.Edges.Select(edge => edge.Label).Should().Equal(shuffled.Edges.Select(edge => edge.Label));
        Signature(_engine.Compute(model)).Should().Be(Signature(_engine.Compute(shuffled)));
    }

    [Fact]
    public void The_same_name_and_state_draw_the_same_card_in_two_catalogs()
    {
        var orders = CatalogGraphFixture.TableId("normalised", "orders");

        var smallModel = Analyze(CatalogGraphFixture.Create()
            .Table("normalised", "orders", 1_000));
        var giantModel = Analyze(CatalogGraphFixture.Create()
            .Table("normalised", "orders", 1_000)
            .Table("normalised", "events", 1_000_000_000));

        var small = _engine.Compute(smallModel).Nodes[smallModel.NodeIndexesById[orders]];
        var giant = _engine.Compute(giantModel).Nodes[giantModel.NodeIndexesById[orders]];

        small.Width.Should().Be(giant.Width, "no card is normalised against this catalog's largest table");
        small.Height.Should().Be(giant.Height);
        small.Width.Should().Be(GraphCard.WidthOf(smallModel.Nodes[small.NodeIndex], Words));
    }

    // ── T12: the card ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_card_is_exactly_as_wide_as_the_longest_line_printed_inside_it()
    {
        var model = Analyze(CatalogGraphFixture.Create()
            .Table("sized", "id")
            .Table("sized", "a_considerably_longer_table_name"));

        var layout = _engine.Compute(model);
        var shortName = layout.Nodes[model.NodeIndexesById[CatalogGraphFixture.TableId("sized", "id")]];
        var longName = layout.Nodes[model.NodeIndexesById[CatalogGraphFixture.TableId("sized", "a_considerably_longer_table_name")]];

        shortName.Width.Should().Be(GraphCard.WidthOf(model.Nodes[shortName.NodeIndex], Words));
        longName.Width.Should().Be(GraphCard.WidthOf(model.Nodes[longName.NodeIndex], Words));
        longName.Width.Should().BeGreaterThan(shortName.Width, "a name must be read, so it widens its own card");
        layout.Nodes.Should().OnlyContain(
            node => node.Height == GraphCard.Height,
            "every card is the same height, so no dimension states a figure");
    }

    [Fact]
    public void No_dimension_states_the_row_count()
    {
        var one = Analyze(CatalogGraphFixture.Create().Table("counted", "orders", 1));
        var many = Analyze(CatalogGraphFixture.Create().Table("counted", "orders", 1_000_000_000));

        var small = _engine.Compute(one).Nodes.Single();
        var large = _engine.Compute(many).Nodes.Single();

        small.Height.Should().Be(large.Height, "a card's height never encodes a figure");
        small.Width.Should().Be(GraphCard.Width("orders", "1 row"));
        large.Width.Should().Be(
            GraphCard.Width("orders", "1,000,000,000 rows"),
            "the width follows the words of the count, never its magnitude");
    }

    [Fact]
    public void The_states_a_table_can_be_in_draw_the_same_shape_and_print_their_own_words()
    {
        var noData = Analyze(CatalogGraphFixture.Create().Table("states", "no_data"));
        var empty = Analyze(CatalogGraphFixture.Create().Table("states", "empty", 0));
        var extreme = Analyze(CatalogGraphFixture.Create().Table("states", "extreme", 1_000_000_000));

        var shapes = new[] { noData, empty, extreme }
            .Select(model => _engine.Compute(model).Nodes.Single())
            .ToList();

        shapes.Should().OnlyContain(node => node.Height == GraphCard.Height);
        shapes[0].Width.Should().Be(GraphCard.Width("no_data", "no data captured"));
        shapes[1].Width.Should().Be(GraphCard.Width("empty", "0 rows"));
        shapes[2].Width.Should().Be(GraphCard.Width("extreme", "1,000,000,000 rows"));
    }

    [Fact]
    public void The_layout_is_the_same_under_a_non_invariant_culture()
    {
        var model = Analyze(Scale(40, 90, 6));
        var invariant = Signature(_engine.Compute(model));

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Signature(_engine.Compute(model)).Should().Be(invariant);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── T17: scale ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_thousand_table_catalog_lays_out_every_node_and_every_edge()
    {
        var model = Analyze(Scale(1_000, 3_000, 40));
        var layout = _engine.Compute(model);

        model.Nodes.Should().HaveCount(1_000);
        model.Edges.Should().HaveCount(3_000);
        layout.Nodes.Should().HaveCount(1_000);
        layout.Edges.Should().HaveCount(3_000);
        layout.Nodes.Should().OnlyContain(node => node.Width > 0 && node.Height > 0 && double.IsFinite(node.TopLeft.X) && double.IsFinite(node.TopLeft.Y));
        layout.Nodes.Select(node => node.NodeIndex).Should().BeEquivalentTo(Enumerable.Range(0, 1_000));
        layout.Edges.Should().OnlyContain(route => route.Points.Count >= 2);
        model.CyclicGroups.Should().ContainSingle().Which.MemberNodeIndexes.Should().HaveCount(40);
    }

    [Fact]
    public void The_scale_catalog_lays_out_identically_on_a_second_run()
    {
        var model = Analyze(Scale(1_000, 3_000, 40));

        Signature(_engine.Compute(model)).Should().Be(Signature(_engine.Compute(model)));
    }

    [Fact]
    public void A_long_reference_chain_lays_out_without_recursing()
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < 2_000; table++)
        {
            fixture.Table("chain", $"t{table:0000}", table, Column("parent_id"));
        }

        for (var table = 0; table < 1_999; table++)
        {
            fixture.Edge(Id("chain", table, 4), Id("chain", table + 1, 4), $"fk_{table:0000}", ["parent_id"]);
        }

        var model = Analyze(fixture);
        var layout = _engine.Compute(model);

        layout.Nodes.Should().HaveCount(2_000);
        layout.Edges.Should().HaveCount(1_999);
        layout.Nodes.Should().OnlyContain(node => double.IsFinite(node.TopLeft.X) && double.IsFinite(node.TopLeft.Y));
        layout.Nodes.Select(node => node.TopLeft.Y).Distinct().Should().HaveCountGreaterThan(1);

        // A chain is drawn as a chain: every table is alone in its band, so the picture is one card wide
        // and grows downwards instead of being folded back across the page.
        layout.Width.Should().BeLessThan(
            3 * (GraphCard.MinWidth + GraphRankedPlacement.ColumnGap),
            "a chain puts one table in each band");
        model.Edges.Should().OnlyContain(
            edge => layout.Nodes[edge.ParentIndex].TopLeft.Y < layout.Nodes[edge.ChildIndex].TopLeft.Y);
    }

    // ── T25: no arrow over a card, and a fan-in stays around its parent ───────────

    [Fact]
    public void No_arrow_is_drawn_across_a_card_it_does_not_join()
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < 6; table++)
        {
            fixture.Table("cross", $"t{table}", 10, Column("parent_id"));
        }

        for (var table = 0; table < 6; table++)
        {
            fixture.Edge(Id("cross", table), Id("cross", (table + 1) % 6), $"fk_ring_{table}", ["parent_id"]);
        }

        fixture.Edge(Id("cross", 0), Id("cross", 3), "fk_chord_0", ["parent_id"]);
        fixture.Edge(Id("cross", 2), Id("cross", 5), "fk_chord_2", ["parent_id"]);

        for (var table = 6; table < 14; table++)
        {
            fixture.Table("cross", $"t{table}", 10, Column("parent_id"));
            fixture.Edge(Id("cross", table), Id("cross", 1), $"fk_fan_{table}", ["parent_id"]);
        }

        var model = Analyze(fixture);
        var layout = _engine.Compute(model);

        AssertNoRouteCrossesACard(layout, model);
    }

    [Fact]
    public void A_blocked_straight_way_is_drawn_around_the_card_that_stands_in_it()
    {
        // One column of three: the middle card stands on the way between the outer two.
        var fixture = CatalogGraphFixture.Create()
            .Table("tower", "top", 10, Column("parent_id"))
            .Table("tower", "middle", 10, Column("parent_id"))
            .Table("tower", "bottom", 10, Column("parent_id"))
            .Edge(CatalogGraphFixture.TableId("tower", "middle"), CatalogGraphFixture.TableId("tower", "top"), "fk_middle_top", ["parent_id"])
            .Edge(CatalogGraphFixture.TableId("tower", "bottom"), CatalogGraphFixture.TableId("tower", "top"), "fk_bottom_top", ["parent_id"]);

        var model = Analyze(fixture);
        var top = model.NodeIndexesById[CatalogGraphFixture.TableId("tower", "top")];
        var middle = model.NodeIndexesById[CatalogGraphFixture.TableId("tower", "middle")];
        var bottom = model.NodeIndexesById[CatalogGraphFixture.TableId("tower", "bottom")];

        var placement = new NodePlacement[3];
        placement[top] = new NodePlacement(top, new GraphPoint(60, 60), 100, GraphCard.Height);
        placement[middle] = new NodePlacement(middle, new GraphPoint(60, 134), 100, GraphCard.Height);
        placement[bottom] = new NodePlacement(bottom, new GraphPoint(60, 208), 100, GraphCard.Height);

        var rows = new int[3];
        rows[top] = 0;
        rows[middle] = 1;
        rows[bottom] = 2;

        var grid = new GraphGrid(
            [60],
            [100],
            [60, 134, 208],
            [30, 190],
            [43, 117, 191, 265],
            [0, 0, 0],
            rows,
            220,
            308);

        var blocked = model.Edges.Single(edge => edge.ChildIndex == bottom && edge.ParentIndex == top);
        var route = GraphEdgeRouter.Route(model, placement, grid)[blocked.Index];

        route.Shape.Should().Be(EEdgeShape.Line);
        route.Points.Should().HaveCountGreaterThan(2,
            "the way turns in the empty corridor rather than being drawn across the card in between");

        var crossed = route.Points.Any(point => GraphGeometry.Contains(placement[middle], point));
        for (var index = 0; index + 1 < route.Points.Count; index++)
        {
            for (var step = 0; step <= 40; step++)
            {
                var share = step / 40.0;
                var point = new GraphPoint(
                    route.Points[index].X + ((route.Points[index + 1].X - route.Points[index].X) * share),
                    route.Points[index].Y + ((route.Points[index + 1].Y - route.Points[index].Y) * share));
                crossed |= GraphGeometry.Contains(placement[middle], point);
            }
        }

        crossed.Should().BeFalse("no path may cross a card it does not join");
    }

    [Fact]
    public void A_fan_wider_than_one_corridor_is_drawn_on_both_sides_of_its_parent()
    {
        var fixture = CatalogGraphFixture.Create().Table("fan", "parent", 7);
        for (var child = 0; child < 20; child++)
        {
            fixture.Table("fan", $"child{child:00}", 3, Column("parent_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("fan", $"child{child:00}"),
                CatalogGraphFixture.TableId("fan", "parent"),
                $"fk_child_{child:00}",
                ["parent_id"]);
        }

        var model = Analyze(fixture);
        var grid = GridOf(model);
        var parent = model.NodeIndexesById[CatalogGraphFixture.TableId("fan", "parent")];
        var children = model.Nodes.Where(node => node.Index != parent).Select(node => node.Index).ToList();

        var below = children.Where(child => grid.RowOfNode[child] > grid.RowOfNode[parent]).ToList();
        var above = children.Where(child => grid.RowOfNode[child] < grid.RowOfNode[parent]).ToList();

        children.Should().OnlyContain(child => grid.RowOfNode[child] != grid.RowOfNode[parent],
            "a child is drawn on a side of the parent it points at, never in the parent's own row");

        // The corridor beside a card gives a run a line per step of its ladder, and a fan wider than that
        // draws two arrows as one stroke if every child is stacked on one side of the parent. A table
        // joined to nothing else asks for nothing but its own arrow, so the overflow is drawn on the
        // parent's free side instead (see GraphRankedPlacement.Spread).
        above.Should().NotBeEmpty("a fan wider than one corridor is spread over both sides of the parent");
        below.Count.Should().BeLessThanOrEqualTo(
            GraphEdgeRouter.Ladder, "one side of a card gives a run a line per step of the ladder, and no more");
        above.Count.Should().BeLessThanOrEqualTo(GraphEdgeRouter.Ladder);

        grid.ColumnOfNode[parent].Should().BeInRange(
            children.Min(child => grid.ColumnOfNode[child]),
            children.Max(child => grid.ColumnOfNode[child]),
            "the parent is drawn over its children's own span, not off at one end of it");

        // Twenty children do not fit one line of the page, so each side of the fan is drawn as one line or
        // more. A line is a run of the fan: no child stands apart from its neighbour, and no line stands
        // apart from the line beside it. A line narrower than the one beside it is centred under the
        // parent, which is what leaves the empty cells at its ends — a hole a reader reads as a gap in
        // the fan only where it separates two children of it.
        foreach (var side in new[] { above, below })
        {
            var lines = side
                .GroupBy(child => grid.RowOfNode[child])
                .OrderBy(line => line.Key)
                .ToList();

            for (var index = 0; index < lines.Count; index++)
            {
                var packed = lines[index].Select(child => grid.ColumnOfNode[child]).Order().ToList();
                packed[^1].Should().Be(
                    packed[0] + packed.Count - 1,
                    "the children of one line are packed one after another with no hole between them");

                if (index > 0)
                {
                    lines[index].Key.Should().Be(
                        lines[index - 1].Key + 1,
                        "the lines of one fan follow one another with no empty row between them");
                }
            }
        }
    }

    [Fact]
    public void A_table_joined_to_one_table_is_drawn_above_the_hub_it_points_at()
    {
        // The reported picture: a hub a dozen tables point at, most of them joined to it and to nothing
        // else, every arrow stacked under it while the band over it stood empty. A table joined by one
        // relation asks for nothing but its own arrow, so it is drawn on the hub's free side — one row
        // over, beside the table it joins — and each arrow keeps a line of its own.
        var tenants = CatalogGraphFixture.TableId("public", "tenants");
        var fixture = CatalogGraphFixture.Create().Table("public", "tenants", 400, Column("plan_id"));

        // Two children answer to a table of their own, so they keep the band their references put them in.
        foreach (var kept in new[] { "drinks", "dishes" })
        {
            var child = CatalogGraphFixture.TableId("public", kept);
            fixture.Table("public", kept, 60, Column("tenant_id"));
            fixture.Table("public", $"{kept}_revisions", 20, Column("tenant_id"));
            fixture.Edge(child, tenants, $"fk_{kept}_tenants", ["tenant_id"]);
            fixture.Edge(
                CatalogGraphFixture.TableId("public", $"{kept}_revisions"),
                child,
                $"fk_{kept}_revisions_{kept}",
                ["tenant_id"]);
        }

        for (var leaf = 0; leaf < 14; leaf++)
        {
            var table = $"leaf_{leaf:00}";
            fixture.Table("public", table, 10, Column("tenant_id"));
            fixture.Edge(CatalogGraphFixture.TableId("public", table), tenants, $"fk_{table}_tenants", ["tenant_id"]);
        }

        var model = Analyze(fixture);
        var grid = GridOf(model);
        var layout = _engine.Compute(model);

        var hub = model.NodeIndexesById[tenants];
        var children = model.Nodes.Where(node => node.Index != hub).Select(node => node.Index).ToList();
        var above = children.Where(child => grid.RowOfNode[child] < grid.RowOfNode[hub]).ToList();
        var below = children.Where(child => grid.RowOfNode[child] > grid.RowOfNode[hub]).ToList();

        above.Should().HaveCountGreaterThanOrEqualTo(2,
            "the tables joined to the hub by one relation only are drawn on the side that has room for them");
        above.Should().OnlyContain(child => grid.RowOfNode[child] + 1 == grid.RowOfNode[hub],
            "a table moved to the hub's free side stays beside it, one row over");

        above.Count.Should().BeLessThanOrEqualTo(
            GraphEdgeRouter.Ladder, "one side of a card gives a run a line per step of the ladder, and no more");

        foreach (var kept in new[] { "drinks", "dishes" })
        {
            grid.RowOfNode[model.NodeIndexesById[CatalogGraphFixture.TableId("public", kept)]]
                .Should().BeGreaterThan(
                    grid.RowOfNode[hub],
                    "a table that answers to another table keeps the band its references put it in");
        }

        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    [Fact]
    public void A_child_that_fits_beside_its_siblings_is_not_drawn_under_them()
    {
        // The auth catalog of nine tables: one table five tables point at, and one table two of them
        // point at. Six children fit a line, so the seventh is not wrapped onto a line of its own —
        // a wrapped band draws that child under its own siblings, and its arrow then has to climb
        // past a row of cards it does not join, which the picture can avoid by being one column wider.
        var users = CatalogGraphFixture.TableId("auth", "users");
        var providers = CatalogGraphFixture.TableId("auth", "sso_providers");
        var fixture = CatalogGraphFixture.Create()
            .Table("auth", "users", 1200, Column("email"))
            .Table("auth", "identities", 800, Column("user_id"))
            .Table("auth", "sso_providers", 4)
            .Table("auth", "sso_domains", 6, Column("sso_provider_id"))
            .Table("auth", "saml_providers", 3, Column("sso_provider_id"))
            .Table("auth", "oauth_consents", 40, Column("user_id"))
            .Table("auth", "one_time_tokens", 90, Column("user_id"))
            .Table("auth", "webauthn_challenges", 12, Column("user_id"))
            .Table("public", "users", 1200);

        fixture.Edge(CatalogGraphFixture.TableId("auth", "identities"), users, "fk_identities_users", ["user_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "oauth_consents"), users, "fk_consents_users", ["user_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "one_time_tokens"), users, "fk_tokens_users", ["user_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "webauthn_challenges"), users, "fk_webauthn_users", ["user_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("public", "users"), users, "fk_public_users", ["id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "sso_domains"), providers, "fk_domains_providers", ["sso_provider_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "saml_providers"), providers, "fk_saml_providers", ["sso_provider_id"]);

        var model = Analyze(fixture);
        var grid = GridOf(model);
        var layout = _engine.Compute(model);

        layout.Edges.Should().OnlyContain(
            route => route.Shape == EEdgeShape.Line && route.Points.Count == 2,
            "a child that fits beside its siblings is joined to its parent by one straight arrow");

        var children = new[] { "identities", "oauth_consents", "one_time_tokens", "webauthn_challenges" }
            .Select(table => model.NodeIndexesById[CatalogGraphFixture.TableId("auth", table)])
            .Append(model.NodeIndexesById[CatalogGraphFixture.TableId("public", "users")])
            .ToList();

        children.Select(child => grid.RowOfNode[child]).Distinct().Should().HaveCount(
            1,
            "the children of one table stand on one line under it");
    }

    [Fact]
    public void A_table_is_drawn_in_the_column_of_the_table_it_points_at()
    {
        // Three roots in the top band, one child under the first of them, one table joined to the
        // three roots below the child, and enough tables nothing joins for the picture to be four
        // columns wide. Centring the one-unit child band would draw the child in column 1 while its
        // own parent stands in column 0, and pay for the misalignment with two extra turns.
        var fixture = CatalogGraphFixture.Create()
            .Table("slide", "root_a", 10)
            .Table("slide", "root_b", 10)
            .Table("slide", "root_c", 10)
            .Table("slide", "child", 4, Column("parent_id"))
            .Table("slide", "settled", 4, Column("child_id"), Column("root_b_id"), Column("root_c_id"))
            .Edge(Table("child"), Table("root_a"), "fk_child_root_a", ["parent_id"])
            .Edge(Table("settled"), Table("child"), "fk_settled_child", ["child_id"])
            .Edge(Table("settled"), Table("root_b"), "fk_settled_root_b", ["root_b_id"])
            .Edge(Table("settled"), Table("root_c"), "fk_settled_root_c", ["root_c_id"]);

        for (var loner = 0; loner < 24; loner++)
        {
            fixture.Table("slide", $"loner{loner:00}", 1);
        }

        var model = Analyze(fixture);
        var grid = GridOf(model);
        var child = model.NodeIndexesById[Table("child")];
        var root = model.NodeIndexesById[Table("root_a")];

        grid.ColumnLefts.Should().HaveCount(4, "the fixture is sized for a four-column page");
        grid.ColumnOfNode[root].Should().Be(0, "the band of roots starts at the left of the page");
        grid.RowOfNode[child].Should().BeGreaterThan(grid.RowOfNode[root]);
        grid.ColumnOfNode[child].Should().Be(
            grid.ColumnOfNode[root],
            "a band is slid until its tables stand under the tables they are joined to");

        static string Table(string name) => CatalogGraphFixture.TableId("slide", name);
    }

    // ── T26: one route per corridor line ──────────────────────────────────────────

    [Fact]
    public void Two_routes_never_run_along_the_same_corridor_line()
    {
        // Four cards in one column, and two constraints that both have to go around a card in
        // between: without a claim on the corridor line, both take the same side and are drawn as
        // one stroke joining all three tables.
        var fixture = CatalogGraphFixture.Create()
            .Table("lanes", "t0", 10, Column("parent_id"))
            .Table("lanes", "t1", 10, Column("parent_id"))
            .Table("lanes", "t2", 10, Column("parent_id"))
            .Table("lanes", "t3", 10, Column("parent_id"))
            .Edge(Id("lanes", 2), Id("lanes", 0), "fk_t2_t0", ["parent_id"])
            .Edge(Id("lanes", 3), Id("lanes", 0), "fk_t3_t0", ["parent_id"]);

        var model = Analyze(fixture);
        var placement = new NodePlacement[4];
        for (var node = 0; node < placement.Length; node++)
        {
            placement[node] = new NodePlacement(node, new GraphPoint(60, 60 + (node * 74)), 100, GraphCard.Height);
        }

        var grid = new GraphGrid(
            [60],
            [100],
            [60, 134, 208, 282],
            [30, 190],
            [43, 117, 191, 265, 339],
            [0, 0, 0, 0],
            [0, 1, 2, 3],
            220,
            382);

        var routes = GraphEdgeRouter.Route(model, placement, grid);

        routes.Should().HaveCount(2);
        routes.Should().OnlyContain(
            route => route.Points.Count > 2,
            "a card stands on the straight way of both");

        var left = VerticalRuns(routes[0].Points);
        var right = VerticalRuns(routes[1].Points);

        left.Should().NotBeEmpty("the way round turns in a column corridor");
        right.Should().NotBeEmpty();

        // Two runs on one x become one drawn line only where they cover one another. Each arrow leaves
        // its own card on a short stub in that card's border column, and two cards of one column put
        // those stubs in different bands, so the stubs may share an x and still be two strokes.
        right.SelectMany(run => left.Where(other => SharesLine(run, other))).Should().BeEmpty(
            "two routes on one corridor line are one drawn line");
    }

    [Fact]
    public void Every_constraint_keeps_its_columns_beside_its_own_route()
    {
        // The picture prints no column text: it is written into the document, hidden, and revealed
        // when the reader points at an arrow or selects one. Revealing is only useful if the text
        // appears on the line it describes, so the route states a point on itself for the text to
        // sit at, and that point lies inside the picture the layout sized (D4, FR13, T22).
        var model = Analyze(Scale(28, 40, 6));
        var layout = _engine.Compute(model);

        layout.Edges.Should().HaveCount(model.Edges.Count);

        foreach (var route in layout.Edges)
        {
            var left = route.Points.Min(point => point.X);
            var right = route.Points.Max(point => point.X);
            var top = route.Points.Min(point => point.Y);
            var bottom = route.Points.Max(point => point.Y);

            route.LabelPoint.X.Should().BeInRange(left, right, "edge {0} names its columns on its own route", route.EdgeIndex);
            route.LabelPoint.Y.Should().BeInRange(top, bottom, "edge {0} names its columns on its own route", route.EdgeIndex);
            route.LabelPoint.X.Should().BeInRange(0, layout.Width);
            route.LabelPoint.Y.Should().BeInRange(0, layout.Height);
        }
    }

    // ── T27: every arrow end owns a point of the border ───────────────────────────

    [Fact]
    public void No_two_arrows_of_one_card_end_on_the_same_border_point()
    {
        // Twenty tables name one parent, and every one of them stands below it: the line from centre to
        // centre lands all twenty on one point of the parent's border, which is drawn as one thick
        // arrow belonging to no constraint in particular.
        var model = Analyze(TwentyToOneParent());
        var layout = _engine.Compute(model);
        var ends = GraphEdgePorts.Assign(model, layout.Nodes);

        var owned = model.Edges
            .SelectMany(edge => new[]
            {
                (Node: edge.ChildIndex, End: ends[edge.Index].Child),
                (Node: edge.ParentIndex, End: ends[edge.Index].Parent),
            })
            .GroupBy(end => end.Node)
            .ToList();

        owned.Should().HaveCount(model.Nodes.Count, "every card that is pointed at or points at another owns its own points");

        foreach (var group in owned)
        {
            group.Select(end => (end.End.Point.X, end.End.Point.Y)).Distinct().Should().HaveCount(
                group.Count(), "table {0} prints no two arrowheads on one border point", group.Key);
        }
    }

    [Fact]
    public void Arrow_ends_of_one_card_stand_a_mark_apart_as_the_page_draws_them()
    {
        // Distinct points are not enough, and neither is a step counted along the border: the two are
        // the same only where the border runs straight. A step that fits the ink without a gap printed
        // two heads as one thick mark, and the length of that step is pinned against the mark itself by
        // A_mark_never_prints_on_the_mark_of_the_end_beside_it. This pins the dealing: three arrows
        // that all land on one point of a card are drawn a whole step apart, not merely somewhere else.
        var parent = CatalogGraphFixture.TableId("apart", "parent");
        var fixture = CatalogGraphFixture.Create().Table("apart", "parent", 7);

        for (var child = 0; child < 3; child++)
        {
            fixture.Table("apart", $"child{child}", 3, Column("parent_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("apart", $"child{child}"),
                parent,
                $"fk_apart_{child}",
                ["parent_id"]);
        }

        var model = Analyze(fixture);
        var hub = model.NodeIndexesById[parent];

        // The picture that merges the heads is one column of cards: three tables stand directly under
        // the one they name, so all three centre-to-centre lines meet the parent's border on the same
        // point and the border alone decides how far apart their heads are drawn.
        var placement = new NodePlacement[model.Nodes.Count];
        placement[hub] = At(hub, 0);

        for (var child = 0; child < 3; child++)
        {
            var index = model.NodeIndexesById[CatalogGraphFixture.TableId("apart", $"child{child}")];
            placement[index] = At(index, 200 + (child * 100));
        }

        var ends = GraphEdgePorts.Assign(model, placement);

        var points = model.Edges
            .Where(edge => edge.ParentIndex == hub)
            .Select(edge => ends[edge.Index].Parent.Point)
            .ToList();

        points.Should().HaveCount(3);

        for (var first = 0; first < points.Count; first++)
        {
            for (var second = first + 1; second < points.Count; second++)
            {
                Space(points[first], points[second]).Should().BeGreaterThanOrEqualTo(
                    GraphEdgePorts.MinStep - 0.5,
                    "two heads printed on one card read as one thick arrow, so the border deals out its whole step");
            }
        }

        static double Space(GraphPoint first, GraphPoint second) =>
            Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

        NodePlacement At(int node, double top) =>
            new(node, new GraphPoint(0, top), GraphCard.WidthOf(model.Nodes[node], Words), GraphCard.Height);
    }

    [Fact]
    public void An_arrow_arrives_on_the_side_the_table_it_comes_from_stands_on()
    {
        // The picture that reported it: one table is named by five others standing side by side under
        // it, so all five arrivals ask for the same point of its border. The border was dealt by
        // walking the loop from its widest gap and carrying every end that did not fit onward round
        // the card, so four arrows arrived at the top of a table every one of them stands below, and
        // the router had to climb over the picture's own margin to come back down into it.
        var parent = CatalogGraphFixture.TableId("under", "parent");
        var fixture = CatalogGraphFixture.Create().Table("under", "parent", 9);

        for (var child = 0; child < 4; child++)
        {
            fixture.Table("under", $"child{child}", 3, Column("parent_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("under", $"child{child}"),
                parent,
                $"fk_under_{child}",
                ["parent_id"]);
        }

        var model = Analyze(fixture);
        var hub = model.NodeIndexesById[parent];

        var placement = new NodePlacement[model.Nodes.Count];
        placement[hub] = At(hub, 0);

        for (var child = 0; child < 4; child++)
        {
            var index = model.NodeIndexesById[CatalogGraphFixture.TableId("under", $"child{child}")];
            placement[index] = At(index, 200, child * 130.0);
        }

        var ends = GraphEdgePorts.Assign(model, placement);

        foreach (var edge in model.Edges)
        {
            var card = placement[edge.ChildIndex];
            var named = placement[edge.ParentIndex];

            Faces(card, ends[edge.Index].Child.Side, named).Should().BeTrue(
                "constraint {0} leaves the side of its own card that the table it names stands on", edge.Name);

            Faces(named, ends[edge.Index].Parent.Side, card).Should().BeTrue(
                "constraint {0} arrives at a side of the card its own table stands on, not round the back of it", edge.Name);
        }

        NodePlacement At(int node, double top, double left = 0) =>
            new(node, new GraphPoint(left, top), GraphCard.WidthOf(model.Nodes[node], Words), GraphCard.Height);

        static bool Faces(NodePlacement card, EEdgeSide side, NodePlacement peer) => side switch
        {
            EEdgeSide.Top => peer.Centre.Y <= card.TopLeft.Y,
            EEdgeSide.Bottom => peer.Centre.Y >= card.Bottom,
            EEdgeSide.Left => peer.Centre.X <= card.TopLeft.X,
            _ => peer.Centre.X >= card.Right,
        };
    }

    [Fact]
    public void An_arrow_never_runs_along_or_back_into_its_own_card()
    {
        // An end dealt onto a side is drawn out of that side: a first step along the border reads as a
        // stroke over the table it belongs to, and a first step back into the card hides which table the
        // arrow is drawn from. The step may still be diagonal — an arrow drawn straight at its target
        // leaves on the outside of both cards, which is all the rule asks.
        var model = Analyze(Scale(28, 40, 6));
        var layout = _engine.Compute(model);
        var ends = GraphEdgePorts.Assign(model, layout.Nodes);

        foreach (var edge in model.Edges)
        {
            var route = layout.Edges[edge.Index];
            if (route.Shape != EEdgeShape.Line)
            {
                continue;
            }

            GoesOutward(route.Points[0], route.Points[1], ends[edge.Index].Child).Should().BeTrue(
                "constraint {0} leaves its own card on the outside of the side its end stands on", edge.Name);
            GoesOutward(route.Points[^1], route.Points[^2], ends[edge.Index].Parent).Should().BeTrue(
                "constraint {0} arrives at its own card from the outside of the side its end stands on", edge.Name);
        }
    }

    [Fact]
    public void Two_self_references_keep_their_own_loop_ends()
    {
        var orders = CatalogGraphFixture.TableId("public", "orders");

        var model = Analyze(CatalogGraphFixture.Create()
            .Table("public", "orders", 12, Column("parent_order_id"), Column("superseded_by_id"))
            .Edge(orders, orders, "fk_orders_parent", ["parent_order_id"], ["id"])
            .Edge(orders, orders, "fk_orders_superseded", ["superseded_by_id"], ["id"]));

        var layout = _engine.Compute(model);
        var ends = GraphEdgePorts.Assign(model, layout.Nodes);

        layout.Edges.Should().OnlyContain(route => route.Shape == EEdgeShape.Loop);
        ends.Select(end => end.Child.Point).Distinct().Should().HaveCount(2,
            "two loops out of one card are two arrows, not one arrow naming neither constraint");
        ends.Select(end => end.Parent.Point).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void A_busy_side_spills_onto_the_side_with_room()
    {
        // The same twenty arrows, at the parent's border: one side has room for a handful of them, so
        // the surplus carries on round the card instead of queueing on the side they all face.
        var model = Analyze(TwentyToOneParent());
        var layout = _engine.Compute(model);
        var ends = GraphEdgePorts.Assign(model, layout.Nodes);

        var parent = model.NodeIndexesById[CatalogGraphFixture.TableId("fan", "parent")];
        var arrived = ends.Where((_, index) => model.Edges[index].ParentIndex == parent).Select(end => end.Parent).ToList();

        arrived.Should().HaveCount(20);
        arrived.Select(end => end.Side).Distinct().Should().HaveCountGreaterThan(1,
            "a full side hands the ends it has no room for to the next one");
    }

    [Fact]
    public void An_end_keeps_the_side_its_neighbour_stands_on_and_its_route_a_corridor_of_its_own()
    {
        // The picture that reported it: three constraints meet public.user_templates — one leaves for
        // the table above it, one arrives from the table below, one arrives from the table at its
        // left. The border is dealt by walking the loop from its widest gap, so the end leaving for
        // the table above named a point behind that start; read as a plain number it looked taken and
        // was pushed a whole turn round onto the card's left side. The arrow then had to leave
        // leftwards, where the corridor already carried another route, and was drawn twelve pixels
        // beside it — two strokes a reader cannot tell apart — while the corridor at the card's right
        // stood empty (T26, T27).
        var alliances = CatalogGraphFixture.TableId("public", "alliances");
        var profiles = CatalogGraphFixture.TableId("public", "profiles");
        var actions = CatalogGraphFixture.TableId("public", "alliance_actions");
        var templates = CatalogGraphFixture.TableId("public", "user_templates");
        var entries = CatalogGraphFixture.TableId("public", "template_entries");
        var stats = CatalogGraphFixture.TableId("public", "template_stats");

        var model = Analyze(CatalogGraphFixture.Create()
            .Table("public", "alliances")
            .Table("public", "profiles")
            .Table("public", "alliance_actions")
            .Table("public", "user_templates")
            .Table("public", "template_stats")
            .Table("public", "template_entries")
            .Edge(actions, alliances, "fk_actions_alliances", ["alliance_id"])
            .Edge(entries, profiles, "fk_entries_profiles", ["profile_id"])
            .Edge(entries, templates, "fk_entries_templates", ["template_id"])
            .Edge(templates, profiles, "fk_templates_profiles", ["profile_id"])
            .Edge(stats, templates, "fk_stats_templates", ["template_id"]));

        var (placement, grid) = ReportedPicture(model);
        var ends = GraphEdgePorts.Assign(model, placement);

        int Edge(string name) => model.Edges.Single(edge => edge.Name == name).Index;

        ends[Edge("fk_templates_profiles")].Child.Side.Should().Be(
            EEdgeSide.Top,
            "the table it points at stands above it, so its arrow leaves the top of the card");

        var routes = GraphEdgeRouter.Route(model, placement, grid);
        var sides = new[] { "fk_actions_alliances", "fk_templates_profiles" }
            .Select(name => Corridor(routes[Edge(name)], cardsCentre: 462.5) > 551 ? "right" : "left")
            .ToList();

        sides.Should().BeEquivalentTo(
            ["right", "left"],
            "each long constraint climbs a corridor of its own, never a lane beside the one already drawn");
    }

    // ── T26: the ink, not the shape ───────────────────────────────────────────────

    [Fact]
    public void No_two_arrows_are_drawn_on_one_stroke()
    {
        // A curve used to stand outside the rule: the router neither claimed a cycle's curve nor tested
        // a candidate against one, so a hub inside a cycle drew several constraints on one stroke and
        // the reader could tell neither where one ended nor which table the third joined. The rule is
        // about ink and not about shape — a curve lying along a corridor line is two arrows on one line
        // exactly as a dog-leg lying along it is — so every shape is compared with every other here.
        var requirements = CatalogGraphFixture.TableId("hub", "requirements");
        var versions = CatalogGraphFixture.TableId("hub", "requirement_versions");
        var comments = CatalogGraphFixture.TableId("hub", "comments");
        var projects = CatalogGraphFixture.TableId("hub", "projects");
        var schemas = CatalogGraphFixture.TableId("hub", "data_schemas");
        var signatures = CatalogGraphFixture.TableId("hub", "signatures");

        var fixture = CatalogGraphFixture.Create()
            .Table("hub", "requirements", 839, Column("project_id"), Column("version_id"), Column("parent_id"))
            .Table("hub", "requirement_versions", 12, Column("requirement_id"))
            .Table("hub", "comments", 1, Column("requirement_id"), Column("author_id"))
            .Table("hub", "projects", 50, Column("status_id"))
            .Table("hub", "data_schemas", 50, Column("version_id"))
            .Table("hub", "signatures", 3, Column("requirement_id"))
            .Edge(requirements, projects, "fk_requirements_project", ["project_id"])
            .Edge(versions, requirements, "fk_versions_requirement", ["requirement_id"])
            .Edge(requirements, versions, "fk_requirements_version", ["version_id"])
            .Edge(comments, requirements, "fk_comments_requirement", ["requirement_id"])
            .Edge(comments, comments, "fk_comments_author", ["author_id"])
            .Edge(schemas, versions, "fk_schemas_version", ["version_id"])
            .Edge(signatures, requirements, "fk_signatures_requirement", ["requirement_id"])
            .Edge(signatures, signatures, "fk_signatures_copy", ["requirement_id"]);

        var model = Analyze(fixture);
        var layout = _engine.Compute(model);

        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    [Fact]
    public void A_table_that_many_constraints_reach_is_given_a_border_wide_enough_to_hold_them()
    {
        // The reported picture: one table is the parent of twenty others, so twenty arrows end on it.
        // A card's border holds a point every eighteen pixels and no more, so a hub given the width of
        // its name spread its twenty ends five pixels apart and drew them as a few thick strokes. The
        // border is the one place a reader tells an arrow's end from its neighbour's, and it is reserved
        // before the route is drawn: the card is widened until every end has a point of its own.
        var ledger = CatalogGraphFixture.TableId("hub", "ledger");
        var fixture = CatalogGraphFixture.Create()
            .Table("hub", "ledger", 40_000, Column("batch_id"));

        for (var entry = 0; entry < 20; entry++)
        {
            var table = CatalogGraphFixture.TableId("hub", $"entry_{entry:00}");
            fixture.Table("hub", $"entry_{entry:00}", 1_000, Column("ledger_id"));
            fixture.Edge(table, ledger, $"fk_entry_{entry:00}_ledger", ["ledger_id"]);
        }

        var model = Analyze(fixture);
        var layout = _engine.Compute(model);

        var hub = model.Nodes.Select((node, index) => (node, index)).Single(pair => pair.node.Table == "ledger").index;
        var ports = GraphEdgePorts.Assign(model, [.. layout.Nodes]);
        var ends = model.Edges
            .SelectMany(edge => new[]
            {
                (Node: edge.ChildIndex, Port: ports[edge.Index].Child),
                (Node: edge.ParentIndex, Port: ports[edge.Index].Parent),
            })
            .Where(pair => pair.Node == hub)
            .Select(pair => pair.Port)
            .ToList();

        ends.Should().HaveCount(20);

        foreach (var side in ends.GroupBy(port => port.Side))
        {
            var points = side.Select(port => port.Point).ToList();

            for (var first = 0; first < points.Count; first++)
            {
                for (var second = first + 1; second < points.Count; second++)
                {
                    var (dx, dy) = (points[second].X - points[first].X, points[second].Y - points[first].Y);
                    Math.Sqrt((dx * dx) + (dy * dy)).Should().BeGreaterThanOrEqualTo(
                        GraphEdgePorts.MinStep,
                        $"two of the twenty ends on the {side.Key} side stand closer together than a stroke apart");
                }
            }
        }

        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>The vertical segments of a route: the line each runs along and the stretch of it it covers.</summary>
    private static List<(double X, double From, double To)> VerticalRuns(IReadOnlyList<GraphPoint> points)
    {
        var runs = new List<(double X, double From, double To)>();
        for (var index = 0; index + 1 < points.Count; index++)
        {
            if (Math.Abs(points[index + 1].X - points[index].X) < 0.01)
            {
                runs.Add((
                    Math.Round(points[index].X, 3),
                    Math.Min(points[index].Y, points[index + 1].Y),
                    Math.Max(points[index].Y, points[index + 1].Y)));
            }
        }

        return runs;
    }

    /// <summary>Whether two vertical runs sit on one line and cover one another, which is drawn as one stroke.</summary>
    private static bool SharesLine((double X, double From, double To) run, (double X, double From, double To) other) =>
        Math.Abs(run.X - other.X) < 0.01 && run.From < other.To && other.From < run.To;

    /// <summary>The corridor line a route climbs: its vertical run that stands furthest from the cards it joins.</summary>
    private static double Corridor(EdgeRoute route, double cardsCentre) =>
        VerticalRuns(route.Points).MaxBy(run => Math.Abs(run.X - cardsCentre)).X;

    /// <summary>
    /// The arrangement the reported picture showed, read off the drawing's own coordinates: one column
    /// of five cards with a second card beside the last of them, and the corridors either side.
    /// </summary>
    private static (NodePlacement[] Placement, GraphGrid Grid) ReportedPicture(CatalogGraphModel model)
    {
        var columnOf = new Dictionary<string, int>
        {
            ["alliances"] = 1,
            ["profiles"] = 1,
            ["alliance_actions"] = 1,
            ["user_templates"] = 1,
            ["template_stats"] = 1,
            ["template_entries"] = 0,
        };

        var rowOf = new Dictionary<string, int>
        {
            ["alliances"] = 0,
            ["profiles"] = 1,
            ["alliance_actions"] = 3,
            ["user_templates"] = 5,
            ["template_stats"] = 6,
            ["template_entries"] = 6,
        };

        double[] lefts = [63, 374];
        double[] widths = [184, 177];
        double[] tops = [28, 104, 180, 256, 332, 408, 484];

        var columns = new int[model.Nodes.Count];
        var rows = new int[model.Nodes.Count];
        var placement = new NodePlacement[model.Nodes.Count];

        for (var node = 0; node < model.Nodes.Count; node++)
        {
            columns[node] = columnOf[model.Nodes[node].Table!];
            rows[node] = rowOf[model.Nodes[node].Table!];
            placement[node] = new NodePlacement(
                node, new GraphPoint(lefts[columns[node]], tops[rows[node]]), widths[columns[node]], GraphCard.Height);
        }

        return (placement, new GraphGrid(
            lefts,
            widths,
            tops,
            [3, 310.5, 611.5],
            [-8, 86.5, 162.5, 238.5, 314.5, 390.5, 466.5, 542.5],
            columns,
            rows,
            675,
            583));
    }

    /// <summary>
    /// Whether a route's first step goes out of the card it starts on: it begins at the end the card was
    /// given and moves away from the side that end stands on, rather than along it or back inside.
    /// </summary>
    private static bool GoesOutward(GraphPoint from, GraphPoint to, GraphEdgePort port) =>
        from == port.Point
        && (((to.X - from.X) * port.OutwardX) + ((to.Y - from.Y) * port.OutwardY)) > 0;

    /// <summary>Twenty tables that each point at one parent, which is the busiest single card a picture can make.</summary>
    private static CatalogGraphFixture TwentyToOneParent()
    {
        var fixture = CatalogGraphFixture.Create().Table("fan", "parent", 7);
        for (var child = 0; child < 20; child++)
        {
            fixture.Table("fan", $"child{child:00}", 3, Column("parent_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("fan", $"child{child:00}"),
                CatalogGraphFixture.TableId("fan", "parent"),
                $"fk_child_{child:00}",
                ["parent_id"]);
        }

        return fixture;
    }

    private static RelationshipColumnView Column(string name) =>
        new(name, "integer", false, false, true, false, null);

    private static string Id(string schema, int table, int digits = 1) =>
        CatalogGraphFixture.TableId(schema, $"t{table.ToString($"D{digits}", CultureInfo.InvariantCulture)}");

    private CatalogGraphModel Analyze(CatalogGraphFixture fixture) => _analyzer.Analyze(fixture.Build());

    /// <summary>The lattice the engine placed the cards on, computed the same way so the cells can be asserted.</summary>
    private static GraphGrid GridOf(CatalogGraphModel model) =>
        GraphRankedPlacement.Place(model, [.. model.Nodes.Select(node => GraphCard.WidthOf(node, Words))]).Grid;

    /// <summary>
    /// The arrangement read back off the emitted lattice, one card per cell, so the arrows it would draw
    /// can be measured the way placement measures them.
    /// </summary>
    /// <param name="grid">The lattice the engine emitted.</param>
    /// <param name="model">The analyzed graph the lattice belongs to.</param>
    /// <returns>The arrangement the lattice states.</returns>
    private static GraphArrangement Arrows(GraphGrid grid, CatalogGraphModel model) =>
        new(
            model,
            [.. model.Nodes.Select(node => new List<int> { node.Index })],
            [.. model.Nodes.Select(_ => (Width: 1, Height: 1))],
            [.. grid.RowOfNode],
            [.. grid.ColumnOfNode]);

    /// <summary>How many grid steps separate two cards, counted the way placement counts them.</summary>
    private static int Step(GraphGrid grid, int first, int second) =>
        Math.Max(
            Math.Abs(grid.ColumnOfNode[first] - grid.ColumnOfNode[second]),
            Math.Abs(grid.RowOfNode[first] - grid.RowOfNode[second]));

    /// <summary>
    /// Walks every route and asserts it never passes through a card it does not join: a line and a
    /// dog-leg are sampled along each segment, a curve across the middle of the cubic where its ends
    /// meet their own cards by design. Violations are collected, so one run names every offending
    /// route instead of only the first.
    /// </summary>
    private static void AssertNoRouteCrossesACard(CatalogGraphLayout layout, CatalogGraphModel model)
    {
        var violations = new List<string>();

        foreach (var route in layout.Edges)
        {
            var edge = model.Edges[route.EdgeIndex];
            var others = layout.Nodes
                .Where(node => node.NodeIndex != edge.ChildIndex && node.NodeIndex != edge.ParentIndex)
                .ToList();

            if (route.Shape == EEdgeShape.Line)
            {
                for (var index = 0; index + 1 < route.Points.Count; index++)
                {
                    var from = route.Points[index];
                    var to = route.Points[index + 1];
                    var (dx, dy) = (to.X - from.X, to.Y - from.Y);
                    var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((dx * dx) + (dy * dy))));

                    for (var step = 0; step <= steps; step++)
                    {
                        var share = (double)step / steps;
                        var point = new GraphPoint(from.X + (dx * share), from.Y + (dy * share));

                        foreach (var card in others.Where(card => GraphGeometry.Contains(card, point)))
                        {
                            violations.Add(Violation(edge, route, card.NodeIndex));
                        }
                    }
                }

                continue;
            }

            for (var sample = 3; sample <= 17; sample++)
            {
                var point = CubicAt(route.Points, sample / 20.0);

                foreach (var card in layout.Nodes.Where(card => GraphGeometry.Contains(card, point)))
                {
                    violations.Add(Violation(edge, route, card.NodeIndex));
                }
            }
        }

        violations.Distinct().Should().BeEmpty("no arrow may be drawn across a card it does not join");
    }

    private static string Violation(CatalogGraphEdge edge, EdgeRoute route, int card) =>
        $"edge {edge.Index} ({edge.ChildIndex} -> {edge.ParentIndex}) crosses table {card}: " +
        route.Shape + " " + string.Join(' ', route.Points.Select(point => $"({Round(point.X)},{Round(point.Y)})"));

    private static GraphPoint CubicAt(IReadOnlyList<GraphPoint> points, double t)
    {
        var inverse = 1 - t;

        return new GraphPoint(
            (inverse * inverse * inverse * points[0].X) + (3 * inverse * inverse * t * points[1].X)
                + (3 * inverse * t * t * points[2].X) + (t * t * t * points[3].X),
            (inverse * inverse * inverse * points[0].Y) + (3 * inverse * inverse * t * points[1].Y)
                + (3 * inverse * t * t * points[2].Y) + (t * t * t * points[3].Y));
    }

    private static CatalogGraphFixture Cycle(string schema, int members)
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < members; table++)
        {
            fixture.Table(schema, $"t{table}", 10, Column("parent_id"));
        }

        for (var table = 0; table < members; table++)
        {
            fixture.Edge(Id(schema, table), Id(schema, (table + 1) % members), $"fk_cycle_{table}", ["parent_id"]);
        }

        return fixture;
    }

    /// <summary>
    /// A catalog of the given size: a ring of the given length, one table everything else points at,
    /// and the rest joined by edges that always point at a higher index, so the ring is the only cycle.
    /// </summary>
    private static CatalogGraphFixture Scale(int tables, int edges, int cycleLength)
    {
        var fixture = CatalogGraphFixture.Create();
        for (var table = 0; table < tables; table++)
        {
            fixture.Table("scale", $"t{table:0000}", (long)table * 37, Column("parent_id"));
        }

        var written = 0;
        for (var table = 0; table < cycleLength; table++)
        {
            fixture.Edge(Id("scale", table, 4), Id("scale", (table + 1) % cycleLength, 4), $"fk_cycle_{table:0000}", ["parent_id"]);
            written++;
        }

        var hub = cycleLength;
        var fanIn = Math.Min(tables - hub - 2, (edges - written) / 2);
        for (var step = 0; step < fanIn; step++)
        {
            fixture.Edge(Id("scale", hub + 1 + step, 4), Id("scale", hub, 4), $"fk_hub_{step:0000}", ["parent_id"]);
            written++;
        }

        for (var gap = 1; written < edges; gap++)
        {
            for (var child = hub + 1; child + gap < tables && written < edges; child++)
            {
                fixture.Edge(Id("scale", child, 4), Id("scale", child + gap, 4), $"fk_fill_{child:0000}_{gap:00}", ["parent_id"]);
                written++;
            }
        }

        return fixture;
    }

    /// <summary>The same catalog declared in two orders, so the canonical order can be asserted.</summary>
    private static CatalogGraphFixture DeclaredInOrder(bool ascending)
    {
        var fixture = CatalogGraphFixture.Create();
        var names = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" };
        var ordered = ascending ? names : [.. names.Reverse()];
        foreach (var name in ordered)
        {
            fixture.Table("shuffled", name, name.Length, Column("parent_id"));
        }

        var pairs = ascending
            ? new[] { ("alpha", "beta"), ("gamma", "alpha"), ("delta", "beta"), ("epsilon", "gamma"), ("zeta", "epsilon") }
            : [.. new[] { ("alpha", "beta"), ("gamma", "alpha"), ("delta", "beta"), ("epsilon", "gamma"), ("zeta", "epsilon") }.Reverse()];

        foreach (var (child, parent) in pairs)
        {
            fixture.Edge(CatalogGraphFixture.TableId("shuffled", child), CatalogGraphFixture.TableId("shuffled", parent),
                $"fk_{child}_{parent}", ["parent_id"]);
        }

        return fixture;
    }

    /// <summary>
    /// Every route read as the ink it draws, then every pair compared: two stretches are one stroke when
    /// they head the same way and lie within a mark's gap of one another over a stretch worth drawing —
    /// the case a reader cannot read apart, however the router arrived at it. A crossing is one point of
    /// contact and stays two arrows, so the run has to continue in both directions before it counts.
    /// </summary>
    private static void AssertNoTwoArrowsRunOnOneStroke(CatalogGraphLayout layout)
    {
        var traces = layout.Edges.Select(route => (route.EdgeIndex, Trace: Trace(route))).ToList();
        var violations = new List<string>();

        for (var first = 0; first < traces.Count; first++)
        {
            for (var second = first + 1; second < traces.Count; second++)
            {
                var together = DrawnAlong(traces[first].Trace, traces[second].Trace);
                if (together > 0)
                {
                    violations.Add(
                        $"edges {traces[first].EdgeIndex} and {traces[second].EdgeIndex} are drawn on one stroke for " +
                        $"{Round(together)} px: {Show(layout.Edges[traces[first].EdgeIndex])} / " +
                        $"{Show(layout.Edges[traces[second].EdgeIndex])}");
                }
            }
        }

        violations.Distinct().Should().BeEmpty("two arrows drawn on one line read as one arrow joining four tables");
    }

    /// <summary>A route written out for a failure message: its shape and every point the document paints.</summary>
    private static string Show(EdgeRoute route) =>
        route.Shape + " " + string.Join(' ', route.Points.Select(point => $"({Round(point.X)},{Round(point.Y)})"));

    /// <summary>A route as the paper shows it: points along the stroke, and how far along the stroke each sits.</summary>
    private sealed record InkTrace(IReadOnlyList<GraphPoint> Points, IReadOnlyList<double> Along);

    /// <summary>A route walked at a step small enough to compare two strokes as the eye follows them.</summary>
    private static InkTrace Trace(EdgeRoute route)
    {
        var shape = new List<GraphPoint>();
        if (route.Shape == EEdgeShape.Line)
        {
            shape.AddRange(route.Points);
        }
        else
        {
            for (var sample = 0; sample <= InkSamples; sample++)
            {
                shape.Add(CubicAt(route.Points, (double)sample / InkSamples));
            }
        }

        var points = new List<GraphPoint> { shape[0] };
        var along = new List<double> { 0 };
        var walked = 0.0;

        for (var index = 0; index + 1 < shape.Count; index++)
        {
            var (from, to) = (shape[index], shape[index + 1]);
            var (dx, dy) = (to.X - from.X, to.Y - from.Y);
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            var steps = Math.Max(1, (int)Math.Ceiling(length / InkStep));

            for (var step = 1; step <= steps; step++)
            {
                var share = (double)step / steps;
                points.Add(new GraphPoint(from.X + (dx * share), from.Y + (dy * share)));
                along.Add(walked + (length * share));
            }

            walked += length;
        }

        return new InkTrace(points, along);
    }

    /// <summary>
    /// How long the two routes are drawn on one another: the longest stretch of the first whose points
    /// all lie within a mark's gap of the second and head the same way there, and over which the second
    /// is walked at the same pace — a crossing matches for a point or two and then parts.
    /// </summary>
    private static double DrawnAlong(InkTrace one, InkTrace other)
    {
        var longest = 0.0;
        var run = -1;
        var (entered, left) = (0.0, 0.0);

        for (var index = 0; index < one.Points.Count; index++)
        {
            var near = Nearest(other, one.Points[index]);

            if (near.Distance > GraphEdgeRouter.InkGap || !Aligned(one, index, other, near.Index))
            {
                run = -1;
                continue;
            }

            if (run < 0)
            {
                run = index;
                (entered, left) = (near.Along, near.Along);
            }

            entered = Math.Min(entered, near.Along);
            left = Math.Max(left, near.Along);

            var mine = one.Along[index] - one.Along[run];
            var theirs = left - entered;

            if (mine >= InkRun && theirs >= InkRun)
            {
                longest = Math.Max(longest, Math.Min(mine, theirs));
            }
        }

        return longest;
    }

    /// <summary>The nearest point of a route to a point beside it: how far, how far along, and which point.</summary>
    private static (double Distance, double Along, int Index) Nearest(InkTrace trace, GraphPoint point)
    {
        var best = (Distance: double.MaxValue, Along: 0.0, Index: 0);

        for (var index = 0; index < trace.Points.Count; index++)
        {
            var (dx, dy) = (trace.Points[index].X - point.X, trace.Points[index].Y - point.Y);
            var distance = Math.Sqrt((dx * dx) + (dy * dy));

            if (distance < best.Distance)
            {
                best = (distance, trace.Along[index], index);
            }
        }

        return best;
    }

    /// <summary>
    /// Whether two stretches are drawn the same way at one point. The turn allowed is the router's own:
    /// the rule the picture is drawn by is the rule it is held to, so the test names the constant
    /// rather than a number of its own — a stroke crossing another at a wider angle is read as a
    /// crossing, and the eye is right to read it that way.
    /// </summary>
    private static bool Aligned(InkTrace one, int at, InkTrace other, int nearest)
    {
        var (ux, uy) = Step(one, at);
        var (vx, vy) = Step(other, nearest);
        var lengths = Math.Sqrt((ux * ux) + (uy * uy)) * Math.Sqrt((vx * vx) + (vy * vy));

        return lengths > 0 && Math.Abs((ux * vy) - (uy * vx)) / lengths < GraphEdgeRouter.Parallel;
    }

    /// <summary>The direction a route travels at one of its points.</summary>
    private static (double X, double Y) Step(InkTrace trace, int index)
    {
        var from = trace.Points[Math.Max(0, index - 1)];
        var to = trace.Points[Math.Min(trace.Points.Count - 1, index + 1)];

        return (to.X - from.X, to.Y - from.Y);
    }

    private static string Signature(CatalogGraphLayout layout)
    {
        var builder = new StringBuilder();
        foreach (var node in layout.Nodes)
        {
            builder.Append("n").Append(node.NodeIndex).Append(':')
                .Append(Round(node.TopLeft.X)).Append(',').Append(Round(node.TopLeft.Y)).Append(',')
                .Append(Round(node.Width)).Append('x').Append(Round(node.Height)).Append(';');
        }

        foreach (var route in layout.Edges)
        {
            builder.Append('e').Append(route.EdgeIndex).Append(':').Append(route.Shape);
            foreach (var point in route.Points)
            {
                builder.Append(' ').Append(Round(point.X)).Append(',').Append(Round(point.Y));
            }

            builder.Append(';');
        }

        return builder.Append(Round(layout.Width)).Append('x').Append(Round(layout.Height)).ToString();
    }

    private static string Round(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
