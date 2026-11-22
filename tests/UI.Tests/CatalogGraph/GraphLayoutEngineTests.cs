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
/// tables it is joined to — a table joined to one table only keeps that side however wide the fan of the
/// table it names — cycles are laid out rather than refused, the three route shapes never share a meaning,
/// no arrow is drawn across a card it does not join, two routes never run along one line, and the same
/// catalog draws the same coordinates every time.
/// </summary>
public sealed class GraphLayoutEngineTests
{
    private readonly CatalogGraphAnalyzer _analyzer = new();

    private readonly GraphLayoutEngine _engine = new();

    /// <summary>
    /// The finished picture. Every test here is about a picture that exists, so a request that ends without
    /// one stops the test with the reason the arrangement gave rather than a null dereference further on.
    /// </summary>
    /// <param name="model">The catalog to arrange.</param>
    /// <param name="timeLimit">
    /// The watchdog, for a fixture whose geometry is asserted on the arrangement the search chose rather than
    /// on the one the clock left. A catalog that would spend the ten-second limit hands back a degraded
    /// arrangement, which promises the hard rules and not the quality a finished search reaches; that path has
    /// its own tests in the selector's suite.
    /// </param>
    private CatalogGraphLayout Picture(CatalogGraphModel model, TimeSpan? timeLimit = null)
    {
        var result = _engine.Compute(model, default, timeLimit);

        result.IsSuccess.Should().BeTrue(
            result.Failure is null ? "the arrangement must draw this catalog" : $"the arrangement failed: {result.Failure.Message}");

        return result.Layout!;
    }

    /// <summary>The words a fixture states its origin in: every fixture here is a catalog.</summary>
    private static readonly GraphOriginWords Words = GraphOriginWords.For(EGraphOriginKind.Catalog);

    /// <summary>The inset a port may graze its own card's border by, as the layout's hard check reads it.</summary>
    private const double PortGrazing = 2;

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

        var layout = Picture(model);

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

        var route = Picture(model).Edges.Should().ContainSingle().Subject;

        route.Shape.Should().Be(EEdgeShape.Line, "the dash is reserved for an outside table and nothing else");
    }

    [Fact]
    public void A_cycle_is_drawn_as_a_detour_and_neither_throws_nor_drops_a_node()
    {
        var model = Analyze(Cycle("cycled", 3));

        model.CyclicGroups.Should().ContainSingle().Which.MemberNodeIndexes.Should().HaveCount(3);

        var layout = Picture(model);

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

        var layout = Picture(model);

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
        var layout = Picture(model);

        var members = model.CyclicGroups.Should().ContainSingle().Subject.MemberNodeIndexes;
        var boxes = members.Select(node => layout.Nodes[node]).ToArray();

        AssertNoCardsOverlap(layout);
        AssertNoCurveCrossesACard(layout, model);

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
        var layout = Picture(model);

        model.CyclicGroups.Should().ContainSingle().Which.MemberNodeIndexes.Should().HaveCount(6);
        layout.Nodes.Should().HaveCount(6);
        layout.Edges.Should().HaveCount(8);

        AssertNoCardsOverlap(layout);
        AssertNoCurveCrossesACard(layout, model);
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
        var layout = Picture(model);

        model.CyclicGroups.Should().ContainSingle().Which.MemberNodeIndexes.Should().HaveCount(3);
        layout.Nodes.Should().HaveCount(4);

        var root = model.NodeIndexesById[CatalogGraphFixture.TableId("fed", "root")];
        var ring = model.CyclicGroups.Should().ContainSingle().Subject.MemberNodeIndexes;
        var ringCards = ring.Select(member => layout.Nodes[member]).ToList();
        var card = layout.Nodes[root];

        card.TopLeft.Y.Should().BeGreaterThan(
            ringCards.Max(below => below.Bottom),
            "the one table that references the ring is drawn in the band below it");
        card.Right.Should().BeGreaterThan(ringCards.Min(below => below.TopLeft.X), "and under the ring");
        card.TopLeft.X.Should().BeLessThan(
            ringCards.Max(below => below.Right),
            "not off to one side of it");
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
        var layout = Picture(model);

        int Cell(string id) => model.NodeIndexesById[id];

        Under(layout.Nodes[Cell(invoices)], layout.Nodes[Cell(orders)]).Should().BeTrue(
            "invoices is joined to orders alone, so it is drawn under it");
        Under(layout.Nodes[Cell(payments)], layout.Nodes[Cell(invoices)]).Should().BeTrue(
            "payments is joined to invoices alone, so it is drawn under it");

        // Every arrow of this catalog points down the page and never back up it: the band carries the
        // tables joined to the table above it, and a table at the far side of a band is still below the
        // table it names, never carried over the cards.
        foreach (var edge in model.Edges)
        {
            layout.Nodes[edge.ChildIndex].Centre.Y.Should().BeGreaterThanOrEqualTo(
                layout.Nodes[edge.ParentIndex].Centre.Y,
                "a table is drawn in the band under the table it names: {0}", edge.Label);
        }

        AssertNoCurveCrossesACard(layout, model);
    }

    [Fact]
    public void The_catalog_behind_the_reported_picture_keeps_every_curve_clear_of_a_table()
    {
        // The catalog the last picture was reported from (pass 11): two schemas, nine tables, and one
        // band of three template tables beside the two tables the picture opens with. The page's own
        // width for nine cards was two columns, so that band wrapped and the third template table was
        // drawn on a line of its own — between template_entries and the profile it points at, and
        // between alliance_actions and the same profile. The band needs one column more, so the width is
        // measured against the arrows it draws instead of assumed: the picture is a page, wider than it is
        // tall, the chords run behind the cards they cross and no curve enters one.
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
        var layout = Picture(model);

        (layout.Width / layout.Height).Should().BeGreaterThan(
            1.0, "the picture is a page: wider than it is tall");

        AssertNoCurveCrossesACard(layout, model);
    }

    // ── T5: determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void The_same_catalog_lays_out_to_the_same_coordinates_every_time()
    {
        var model = Analyze(Scale(60, 180, 8));

        Signature(Picture(model)).Should().Be(Signature(Picture(model)));
    }

    [Fact]
    public void A_shuffled_input_lays_out_to_the_same_coordinates()
    {
        var model = Analyze(DeclaredInOrder(ascending: true));
        var shuffled = Analyze(DeclaredInOrder(ascending: false));

        model.Nodes.Select(node => node.Id).Should().Equal(shuffled.Nodes.Select(node => node.Id));
        model.Edges.Select(edge => edge.Label).Should().Equal(shuffled.Edges.Select(edge => edge.Label));
        Signature(Picture(model)).Should().Be(Signature(Picture(shuffled)));
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

        var small = Picture(smallModel).Nodes[smallModel.NodeIndexesById[orders]];
        var giant = Picture(giantModel).Nodes[giantModel.NodeIndexesById[orders]];

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

        var layout = Picture(model);
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

        var small = Picture(one).Nodes.Single();
        var large = Picture(many).Nodes.Single();

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
            .Select(model => Picture(model).Nodes.Single())
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
        var invariant = Signature(Picture(model));

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Signature(Picture(model)).Should().Be(invariant);
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
        var layout = Picture(model);

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

        Signature(Picture(model)).Should().Be(Signature(Picture(model)));
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
        var layout = Picture(model);

        layout.Nodes.Should().HaveCount(2_000);
        layout.Edges.Should().HaveCount(1_999);
        layout.Nodes.Should().OnlyContain(node => double.IsFinite(node.TopLeft.X) && double.IsFinite(node.TopLeft.Y));
        layout.Nodes.Select(node => node.TopLeft.Y).Distinct().Should().HaveCountGreaterThan(1);

        // A chain is drawn as a chain: every table is alone in its band, so the picture is one card wide
        // and grows downwards instead of being folded back across the page.
        layout.Width.Should().BeLessThan(
            3 * (GraphCard.MinWidth + GraphPlacementConstants.ColumnGap),
            "a chain puts one table in each band");
        model.Edges.Should().OnlyContain(
            edge => layout.Nodes[edge.ParentIndex].TopLeft.Y < layout.Nodes[edge.ChildIndex].TopLeft.Y);
    }

    // ── T25: no arrow over a card, and a fan-in stays around its parent ───────────

    [Fact]
    public void A_chord_runs_behind_a_card_and_no_curve_enters_one_it_does_not_join()
    {
        // The cards are painted over the ink, so a chord passing behind a table it does not join is the
        // picture, not a fault; a curve is hidden by nothing, so it still may not enter a card.
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
        var layout = Picture(model);

        var behind = layout.Edges
            .Where(route => route.Geometry == EEdgeGeometry.Straight)
            .Count(route => CardsUnder(layout, model, route).Count > 0);
        behind.Should().BeGreaterThan(
            0,
            "a catalog of one ring and a fan draws chords behind the tables that stand in their way");

        AssertNoCurveCrossesACard(layout, model);
    }

    [Fact]
    public void A_card_standing_on_a_straight_way_is_passed_behind_it()
    {
        // Three tables stand on one line under the table they name, so the relation from the last of them
        // back to the first has one table standing on its straight way. The relation stays the chord between
        // its two ports and the card is painted over it — never a bow round the card, and never a set of
        // right-angle turns drawn through the corridors between the cards.
        var fixture = CatalogGraphFixture.Create().Table("bowed", "parent", 7);
        for (var child = 0; child < 3; child++)
        {
            fixture.Table("bowed", $"child{child:00}", 3, Column("parent_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("bowed", $"child{child:00}"),
                CatalogGraphFixture.TableId("bowed", "parent"),
                $"fk_child_{child:00}",
                ["parent_id"]);
        }

        fixture.Edge(
            CatalogGraphFixture.TableId("bowed", "child02"),
            CatalogGraphFixture.TableId("bowed", "child00"),
            "fk_child02_child00",
            ["parent_id"]);

        var model = Analyze(fixture);
        var layout = Picture(model);

        var blocked = model.Edges.Single(edge => edge.Name == "fk_child02_child00");
        var route = layout.Edges[blocked.Index];

        route.Shape.Should().Be(EEdgeShape.Line, "no cycle closes here, so the line style is the plain one");
        route.Geometry.Should().Be(
            EEdgeGeometry.Straight,
            "the chord between the two ports is the picture, and the card in its way is painted over it");
        route.Points.Should().HaveCount(2, "nothing bows: the two ports are the whole relation");

        var behind = CardsUnder(layout, model, route).Distinct().ToList();
        behind.Should().NotBeEmpty("the table standing on the straight way is painted over the chord, not dodged");
        behind.Should().NotContain(blocked.ChildIndex).And.NotContain(blocked.ParentIndex);

        AssertNoCurveCrossesACard(layout, model);
    }

    [Fact]
    public void A_fan_of_twenty_is_drawn_around_the_table_it_names_as_one_ring()
    {
        // A fan wider than the page has one shape that keeps every arrow a straight chord of the same
        // length and the picture on one screen: the ring the approved baseline draws, the table they name
        // standing in the middle of its children. What it must never be is a row — or a pair of rows —
        // several screens long, which is the picture this rule was written for.
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
        var layout = Picture(model);
        var parent = model.NodeIndexesById[CatalogGraphFixture.TableId("fan", "parent")];
        var children = model.Nodes.Where(node => node.Index != parent).Select(node => node.Index).ToList();
        var hub = layout.Nodes[parent];
        var cards = children.Select(child => layout.Nodes[child]).ToList();

        hub.Centre.Y.Should().BeInRange(
            cards.Min(card => card.Centre.Y),
            cards.Max(card => card.Centre.Y),
            "the table a fan names stands among its children, never at the end of a row of them");

        hub.Centre.X.Should().BeInRange(
            cards.Min(card => card.Centre.X),
            cards.Max(card => card.Centre.X),
            "the parent is drawn over its children's own span, not off at one end of it");

        cards.Select(card => card.TopLeft.Y).Distinct().Count().Should().BeGreaterThan(
            1,
            "a fan of twenty tables is wider than one page, so it is dealt in rows, not in one line");

        (layout.Width / layout.Height).Should().BeLessThan(
            6,
            "twenty tables are a band of rows, not a strip stretched over six screens");

        AssertNoCardsOverlap(layout);
        AssertNoCurveCrossesACard(layout, model);
        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    [Fact]
    public void A_table_joined_to_one_table_keeps_the_side_its_relation_asks_for()
    {
        // The reported picture: a hub a dozen tables point at, most of them joined to it and to nothing
        // else. The overflow was once carried to the hub's free side — one row over, but above it — which
        // drew those arrows running up into the child and down into the parent, the opposite of what the
        // catalog says. A table joined by one relation keeps the side its relation asks for, so the whole
        // fan stands under the hub and every arrow climbs the one way a reader follows.
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
        var layout = Picture(model);

        var hub = model.NodeIndexesById[tenants];
        var children = model.Nodes.Where(node => node.Index != hub).Select(node => node.Index).ToList();
        var hubCard = layout.Nodes[hub];

        children.Where(child => layout.Nodes[child].Centre.Y < hubCard.Centre.Y).Should().BeEmpty(
            "a table joined to the hub by one relation is not carried over it: the arrow it draws would "
            + "point the wrong way for the relation the catalog states");
        children.Should().OnlyContain(
            child => layout.Nodes[child].Centre.Y > hubCard.Centre.Y,
            "the whole fan stands on the side its relations ask for");

        foreach (var kept in new[] { "drinks", "dishes" })
        {
            layout.Nodes[model.NodeIndexesById[CatalogGraphFixture.TableId("public", kept)]].Centre.Y
                .Should().BeGreaterThan(
                    hubCard.Centre.Y,
                    "a table that answers to another table keeps the band its references put it in");
        }

        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    [Fact]
    public void The_reported_two_hub_catalog_is_drawn_as_one_compact_picture()
    {
        // The reported picture in its shape: one table a dozen others name, a second hub the first's
        // children name in their turn, and chains hanging under both. A fan that wide is dealt in rows in
        // the band under the table it names, so what is asserted here is that band: the hub over its own
        // fan's span, no card stacking another, no curve entering a card it does not join, and the whole
        // catalog on one screen instead of a strip. The top-down order the reader follows is asserted on
        // the catalogs that can be drawn that way — the chain and the slide below.
        var users = CatalogGraphFixture.TableId("auth", "users");
        var publicUsers = CatalogGraphFixture.TableId("public", "users");
        var projects = CatalogGraphFixture.TableId("public", "projects");
        var schemas = CatalogGraphFixture.TableId("public", "data_schemas");
        var requirements = CatalogGraphFixture.TableId("public", "requirements");

        var fixture = CatalogGraphFixture.Create()
            .Table("auth", "users", 1_200, Column("id"))
            .Table("public", "users", 1_200, Column("id"))
            .Table("public", "projects", 340, Column("owner_id"))
            .Table("public", "data_schemas", 90, Column("project_id"))
            .Table("public", "schema_versions", 220, Column("schema_id"))
            .Table("public", "requirements", 839, Column("project_id"), Column("parent_id"))
            .Table("public", "requirement_versions", 12, Column("requirement_id"))
            .Table("public", "requirement_links", 40, Column("requirement_id"))
            .Table("public", "comments", 60, Column("requirement_id"), Column("author_id"))
            .Table("public", "signatures", 3, Column("requirement_id"));

        // One relation each, straight to the root: thirteen of them, which is more than one side of a
        // card can line up, and the case the free-side answer used to be reached for.
        foreach (var name in new[]
        {
            "identities", "sessions", "oauth_authorizations", "oauth_consents", "mfa_factors",
            "mfa_recovery_codes", "scm_users", "webauthn_credentials", "webauthn_challenges",
            "one_time_tokens", "invitations", "verifications", "receipts",
        })
        {
            var leaf = CatalogGraphFixture.TableId("auth", name);
            fixture.Table("auth", name, 30, Column("user_id"));
            fixture.Edge(leaf, users, $"fk_{name}_users", ["user_id"]);
        }

        fixture.Edge(publicUsers, users, "fk_public_users_users", ["id"]);
        fixture.Edge(projects, users, "fk_projects_users", ["owner_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("public", "schema_versions"), schemas, "fk_versions_schemas", ["schema_id"]);
        fixture.Edge(schemas, projects, "fk_schemas_projects", ["project_id"]);
        fixture.Edge(requirements, projects, "fk_requirements_projects", ["project_id"]);
        fixture.Edge(requirements, requirements, "fk_requirements_parent", ["parent_id"]);
        fixture.Edge(
            CatalogGraphFixture.TableId("public", "requirement_versions"),
            requirements,
            "fk_versions_requirements",
            ["requirement_id"]);
        fixture.Edge(
            CatalogGraphFixture.TableId("public", "requirement_links"),
            requirements,
            "fk_links_requirement",
            ["requirement_id"]);
        fixture.Edge(requirements, publicUsers, "fk_requirements_author", ["parent_id"]);
        fixture.Edge(
            CatalogGraphFixture.TableId("public", "comments"),
            requirements,
            "fk_comments_requirement",
            ["requirement_id"]);
        fixture.Edge(
            CatalogGraphFixture.TableId("public", "comments"),
            publicUsers,
            "fk_comments_author",
            ["author_id"]);
        fixture.Edge(
            CatalogGraphFixture.TableId("public", "signatures"),
            requirements,
            "fk_signatures_requirement",
            ["requirement_id"]);

        var model = Analyze(fixture);
        var layout = Picture(model);

        var hub = model.NodeIndexesById[users];
        var hubCard = layout.Nodes[hub];
        var crowd = model.Edges.Where(edge => edge.ParentIndex == hub)
            .Select(edge => layout.Nodes[edge.ChildIndex])
            .ToList();

        hubCard.Centre.Y.Should().BeLessThan(
            crowd.Min(card => card.Centre.Y),
            "the table thirteen tables name stands in the band over them, never at the end of a row of them");

        hubCard.Centre.X.Should().BeInRange(
            crowd.Min(card => card.Centre.X),
            crowd.Max(card => card.Centre.X),
            "the hub is drawn over its children's own span, not off at one end of it");

        crowd.Select(card => card.TopLeft.Y).Distinct().Count().Should().BeGreaterThan(
            1,
            "thirteen tables are wider than a page, so they are dealt in rows under their parent");

        (layout.Width / layout.Height).Should().BeLessThan(
            6,
            "the whole catalog is one picture, not four rows stretched over six screens");

        AssertNoCardsOverlap(layout);
        AssertNoCurveCrossesACard(layout, model);
        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    [Fact]
    public void A_rank_that_fits_the_page_is_drawn_on_one_line()
    {
        // The auth catalog of seven tables: one table three tables point at, and one table two of them
        // point at. Three children fit a page beside each other, so they stand on one line under the
        // table they name — the picture is only dealt in bands when a rank is wider than the page.
        var users = CatalogGraphFixture.TableId("auth", "users");
        var providers = CatalogGraphFixture.TableId("auth", "sso_providers");
        var fixture = CatalogGraphFixture.Create()
            .Table("auth", "users", 1200, Column("email"))
            .Table("auth", "identities", 800, Column("user_id"))
            .Table("auth", "sso_providers", 4)
            .Table("auth", "sso_domains", 6, Column("sso_provider_id"))
            .Table("auth", "saml_providers", 3, Column("sso_provider_id"))
            .Table("auth", "oauth_consents", 40, Column("user_id"))
            .Table("auth", "one_time_tokens", 90, Column("user_id"));

        fixture.Edge(CatalogGraphFixture.TableId("auth", "identities"), users, "fk_identities_users", ["user_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "oauth_consents"), users, "fk_consents_users", ["user_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "one_time_tokens"), users, "fk_tokens_users", ["user_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "sso_domains"), providers, "fk_domains_providers", ["sso_provider_id"]);
        fixture.Edge(CatalogGraphFixture.TableId("auth", "saml_providers"), providers, "fk_saml_providers", ["sso_provider_id"]);

        var model = Analyze(fixture);
        var layout = Picture(model);

        layout.Edges.Should().OnlyContain(
            route => route.Shape == EEdgeShape.Line && route.Points.Count == 2,
            "a child that fits beside its siblings is joined to its parent by one straight arrow");

        var children = new[] { "identities", "oauth_consents", "one_time_tokens" }
            .Select(table => model.NodeIndexesById[CatalogGraphFixture.TableId("auth", table)])
            .ToList();

        children.Select(child => layout.Nodes[child].TopLeft.Y).Distinct().Should().HaveCount(
            1,
            "the children of one table stand on one line under it while they fit the page");

        AssertNoCardsOverlap(layout);
    }

    [Fact]
    public void A_table_is_drawn_under_the_table_it_points_at()
    {
        // Three roots in the top band, one child under the first of them, one table joined to the
        // three roots below the child, and enough tables nothing joins for the picture to be wide.
        // A band that drifts away from its parent draws the child beside a table it does not join
        // and pays for the misalignment with a long arrow across the picture.
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
        var layout = Picture(model);
        var child = model.NodeIndexesById[Table("child")];
        var root = model.NodeIndexesById[Table("root_a")];
        var roots = new[] { "root_a", "root_b", "root_c" }
            .Select(name => layout.Nodes[model.NodeIndexesById[Table(name)]])
            .ToList();

        roots.Select(card => card.TopLeft.Y).Distinct().Should().HaveCount(1, "the roots stand in one band");
        roots.Min(card => card.TopLeft.X).Should().Be(
            layout.Nodes.Min(card => card.TopLeft.X),
            "the band of roots starts at the left of the page");

        layout.Nodes[child].TopLeft.Y.Should().BeGreaterThan(layout.Nodes[root].TopLeft.Y);
        Overlaps(layout.Nodes[child], layout.Nodes[root]).Should().BeTrue(
            "a band is laid out until its tables stand under the tables they are joined to");

        static string Table(string name) => CatalogGraphFixture.TableId("slide", name);
    }

    // ── T26: two routes, two strokes ──────────────────────────────────────────────

    [Fact]
    public void Two_routes_never_run_along_the_same_corridor_line()
    {
        // Four cards in one column and two constraints that both have to reach past a card in between:
        // whatever way the router bows them, the two relations are drawn apart — each leaves its own
        // card on a point of the border, and no stretch of one is drawn on a stretch of the other.
        var fixture = CatalogGraphFixture.Create()
            .Table("lanes", "t0", 10, Column("parent_id"))
            .Table("lanes", "t1", 10, Column("parent_id"))
            .Table("lanes", "t2", 10, Column("parent_id"))
            .Table("lanes", "t3", 10, Column("parent_id"))
            .Edge(Id("lanes", 2), Id("lanes", 0), "fk_t2_t0", ["parent_id"])
            .Edge(Id("lanes", 3), Id("lanes", 0), "fk_t3_t0", ["parent_id"]);

        var model = Analyze(fixture);
        var layout = Picture(model);

        layout.Edges.Should().HaveCount(2);
        layout.Edges.Select(route => route.Points[0]).Distinct().Should().HaveCount(
            2, "each arrow leaves its own card on a point of its own");

        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    [Fact]
    public void Every_constraint_keeps_its_columns_beside_its_own_route()
    {
        // The picture prints no column text: it is written into the document, hidden, and revealed
        // when the reader points at an arrow or selects one. Revealing is only useful if the text
        // appears on the line it describes, so the route states a point on itself for the text to
        // sit at, and that point lies inside the picture the layout sized (D4, FR13, T22).
        var model = Analyze(Scale(28, 40, 6));
        var layout = Picture(model);

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
        var layout = Picture(model);
        var ports = GraphPortAssigner.Assign(
            layout.Nodes,
            model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());

        ports.IsComplete.Should().BeTrue("a card a fan meets is widened until its border holds every end");

        var owned = model.Edges
            .SelectMany(edge => new[]
            {
                (Node: edge.ChildIndex, End: ports.Ends[edge.Index]!.Child),
                (Node: edge.ParentIndex, End: ports.Ends[edge.Index]!.Parent),
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
        var layout = Picture(model);
        var ports = GraphPortAssigner.Assign(
            layout.Nodes,
            model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());

        ports.IsComplete.Should().BeTrue();

        var points = model.Edges
            .Where(edge => edge.ParentIndex == hub)
            .Select(edge => ports.Ends[edge.Index]!.Parent.Point)
            .ToList();

        points.Should().HaveCount(3);

        for (var first = 0; first < points.Count; first++)
        {
            for (var second = first + 1; second < points.Count; second++)
            {
                Space(points[first], points[second]).Should().BeGreaterThanOrEqualTo(
                    GraphGeometryContract.MinimumPortStep - 0.5,
                    "two heads printed on one card read as one thick arrow, so the border deals out its whole step");
            }
        }

        static double Space(GraphPoint first, GraphPoint second) =>
            Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
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
        var layout = Picture(model);
        var ports = GraphPortAssigner.Assign(
            layout.Nodes,
            model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());

        ports.IsComplete.Should().BeTrue("four ends on a border deal out if the card is widened for them");

        foreach (var edge in model.Edges)
        {
            var card = layout.Nodes[edge.ChildIndex];
            var named = layout.Nodes[edge.ParentIndex];
            var ends = ports.Ends[edge.Index]!;

            Faces(card, ends.Child.Side, named).Should().BeTrue(
                "constraint {0} leaves the side of its own card that the table it names stands on", edge.Name);

            Faces(named, ends.Parent.Side, card).Should().BeTrue(
                "constraint {0} arrives at a side of the card its own table stands on, not round the back of it", edge.Name);
        }

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
        // A route starts and ends on the border of the cards it joins — a port sits there — but its stroke
        // may not run over either card: a first stretch along the border reads as a stroke over the table it
        // belongs to, and a stretch back inside hides which table the arrow is drawn from. The card is read
        // as the layout's own hard check reads it, shrunk by the inset a port is allowed to graze.
        var model = Analyze(Scale(28, 40, 6));
        var layout = Picture(model);

        foreach (var edge in model.Edges)
        {
            var route = layout.Edges[edge.Index];
            var points = Stroke(route);

            foreach (var card in new[] { edge.ChildIndex, edge.ParentIndex }.Distinct())
            {
                var node = layout.Nodes[card];
                for (var at = 1; at < points.Count; at++)
                {
                    GraphGeometry.SegmentHitsRect(points[at - 1], points[at], node, -PortGrazing).Should().BeFalse(
                        "constraint {0} is drawn over table {1}, which it joins", edge.Name, card);
                }
            }
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

        var layout = Picture(model);
        var ports = GraphPortAssigner.Assign(
            layout.Nodes,
            model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());

        ports.IsComplete.Should().BeTrue();

        var ends = model.Edges.Select(edge => ports.Ends[edge.Index]!).ToList();

        layout.Edges.Should().OnlyContain(route => route.Shape == EEdgeShape.Loop);
        ends.Select(end => end.Child.Point).Distinct().Should().HaveCount(2,
            "two loops out of one card are two arrows, not one arrow naming neither constraint");
        ends.Select(end => end.Parent.Point).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void A_fan_of_seven_tables_is_dealt_on_a_card_of_its_own_width()
    {
        // Seven tables name the same one. Every arrow of the fan leaves the one border the seven stand
        // under, and the card is only as wide as that border needs to deal them — not as wide as the row
        // they stand in. A card stretched to the width of its own fan stops reading as a table: the reader
        // sees two lines of text floating in a band and cannot tell a table from a rule.
        var hub = CatalogGraphFixture.TableId("fan", "owner");
        var fixture = CatalogGraphFixture.Create().Table("fan", "owner", 7);
        for (var child = 0; child < 7; child++)
        {
            fixture.Table("fan", $"entry{child:00}", 3, Column("owner_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("fan", $"entry{child:00}"),
                hub,
                $"fk_entry_{child:00}",
                ["owner_id"]);
        }

        var model = Analyze(fixture);
        var layout = Picture(model);
        var ports = GraphPortAssigner.Assign(
            layout.Nodes,
            model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());

        ports.IsComplete.Should().BeTrue("seven ends fit the border of a card");

        var node = model.NodeIndexesById[hub];
        var card = layout.Nodes[node];
        var fan = model.Edges.Where(edge => edge.ParentIndex == node).ToList();
        var spread = fan.Max(edge => layout.Nodes[edge.ChildIndex].Right)
            - fan.Min(edge => layout.Nodes[edge.ChildIndex].TopLeft.X);

        fan.Should().HaveCount(7);
        card.Width.Should().BeLessThanOrEqualTo(
            GraphPortAssigner.BorderWidth(fan.Count),
            "the card is as wide as the border its ends need, never as wide as the row they stand in");
        card.Width.Should().BeLessThan(spread, "and is never stretched across the row of tables it stands over");
        fan.Select(edge => ports.Ends[edge.Index]!.Parent.Point).Distinct().Should().HaveCount(7, "no two ends share a point");
    }

    [Fact]
    public void A_fan_is_dealt_across_the_borders_of_the_table_it_names()
    {
        // Twenty tables name the same one. The card keeps the width its own text and its twenty ends need
        // — a port for each and a step to spare — and the fan reaches it from the sides its tables stand
        // on, each arrow on a port of its own. The card is never stretched across the row of its tables: a
        // table drawn as wide as its fan stops reading as a table and becomes a band of text.
        var model = Analyze(TwentyToOneParent());
        var layout = Picture(model);
        var ports = GraphPortAssigner.Assign(
            layout.Nodes,
            model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());

        ports.IsComplete.Should().BeTrue("a card a fan meets is widened until its border holds every end");

        var parent = model.NodeIndexesById[CatalogGraphFixture.TableId("fan", "parent")];
        var card = layout.Nodes[parent];
        var fan = model.Edges.Where(edge => edge.ParentIndex == parent)
            .OrderBy(edge => layout.Nodes[edge.ChildIndex].Centre.X)
            .ThenBy(edge => edge.Index)
            .ToList();
        var arrived = fan.Select(edge => ports.Ends[edge.Index]!.Parent).ToList();

        var spread = fan.Max(edge => layout.Nodes[edge.ChildIndex].Right)
            - fan.Min(edge => layout.Nodes[edge.ChildIndex].TopLeft.X);

        fan.Should().HaveCount(20);

        card.Width.Should().BeLessThanOrEqualTo(
            GraphPortAssigner.BorderWidth(fan.Count),
            "the card is as wide as the border its ends need, never as wide as the row they stand in");
        card.Width.Should().BeLessThan(spread, "and is never stretched across the row of tables it stands over");
        arrived.Select(end => end.Point).Distinct().Should().HaveCount(20, "no two ends share a point");
    }

    [Fact]
    public void An_end_leaves_the_side_its_neighbour_stands_on_and_every_relation_is_its_own_chord()
    {
        // The picture that reported it: three constraints meet public.user_templates — one leaves for
        // the table above it, one arrives from the table below, one arrives from the table at its
        // left. The end leaving for the table above once named a point behind the start of the border
        // and was pushed a whole turn round onto the card's left side, where the arrow had to leave
        // into a route already drawn and was read as one stroke with it (T26, T27). The side an end
        // leaves is the side its neighbour stands on — whichever band the two cards are dealt in — and
        // every relation then runs port to port as its own chord.
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

        var layout = Picture(model);
        var ports = GraphPortAssigner.Assign(
            layout.Nodes,
            model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());

        ports.IsComplete.Should().BeTrue();

        int Edge(string name) => model.Edges.Single(edge => edge.Name == name).Index;

        var templatesProfiles = ports.Ends[Edge("fk_templates_profiles")]!;
        var child = layout.Nodes[model.Edges[Edge("fk_templates_profiles")].ChildIndex];
        var parent = layout.Nodes[model.Edges[Edge("fk_templates_profiles")].ParentIndex];
        var (facingX, facingY) = Facing(templatesProfiles.Child.Side);

        ((facingX * (parent.Centre.X - child.Centre.X)) + (facingY * (parent.Centre.Y - child.Centre.Y)))
            .Should().BePositive("the side an end leaves is the side its neighbour stands on");

        foreach (var edge in model.Edges)
        {
            var route = layout.Edges[edge.Index];
            var ends = ports.Ends[edge.Index]!;

            route.Points.Should().HaveCount(2, $"{edge.Label} is its own chord, not a bow");
            route.Points[0].Should().Be(ends.Child.Point, $"{edge.Label} leaves the child's port");
            route.Points[^1].Should().Be(ends.Parent.Point, $"{edge.Label} arrives on the parent's port");
        }
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
        var layout = Picture(model);

        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    [Fact]
    public void A_catalog_that_meets_one_table_keeps_every_arrow_apart()
    {
        // The reported picture: most of the catalog names one table, so every arrow of the fan meets the
        // same card. What the picture promises is not a shape — one arrangement serves every catalog — but
        // two rules that hold whatever the arrangement: no arrow across a card it does not join, and no two
        // arrows drawn on one stroke.
        var hub = CatalogGraphFixture.TableId("hub", "ledger");
        var fixture = CatalogGraphFixture.Create()
            .Table("hub", "ledger", 800, Column("id"));

        for (var entry = 0; entry < 8; entry++)
        {
            fixture.Table("hub", $"entry{entry:D2}", 10, Column("ledger_id"));
            fixture.Edge(CatalogGraphFixture.TableId("hub", $"entry{entry:D2}"), hub, $"fk_entry_{entry}", ["ledger_id"]);
        }

        for (var slice = 0; slice < 4; slice++)
        {
            fixture.Table("hub", $"slice{slice:D2}", 10, Column("entry_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("hub", $"slice{slice:D2}"),
                CatalogGraphFixture.TableId("hub", $"entry{slice:D2}"),
                $"fk_slice_{slice}",
                ["entry_id"]);
        }

        fixture.Table("hub", "note00", 10, Column("slice_id"));
        fixture.Edge(CatalogGraphFixture.TableId("hub", "note00"), CatalogGraphFixture.TableId("hub", "slice00"),
            "fk_note_0", ["slice_id"]);

        var model = Analyze(fixture);
        var layout = Picture(model);

        // Assert
        AssertNoCardsOverlap(layout);
        AssertNoCurveCrossesACard(layout, model);
        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    [Fact]
    public void A_cycle_keeps_every_member_and_draws_no_arrow_across_another()
    {
        // The picture a reader assembles by hand: a group of tables that point at one another in a circle.
        // No arrangement gives such a group a top, so what the picture promises is what it promises of every
        // catalog — every member placed, every arrow routed clear of the cards it does not join, no two
        // arrows on one stroke, and the arrows of the circle drawn without crossing one another.
        var fixture = Cycle("ring", 5);
        var tree = CatalogGraphFixture.TableId("ring", "tree");
        var leaf = CatalogGraphFixture.TableId("ring", "leaf");
        fixture.Table("ring", "tree", 220, Column("parent_id"));
        fixture.Edge(tree, tree, "fk_tree_parent", ["parent_id"]);
        fixture.Table("ring", "leaf", 40, Column("tree_id"));
        fixture.Edge(leaf, tree, "fk_leaf_tree", ["tree_id"]);
        fixture.Outside("dt:reports.missing", "reports", "missing");
        fixture.Edge(leaf, "dt:reports.missing", "fk_leaf_missing", ["other_id"]);

        var model = Analyze(fixture);
        var layout = Picture(model);

        layout.Nodes.Should().HaveCount(8, "five members of the circle, the table they hang from, its leaf and the outside table");
        AssertNoCardsOverlap(layout);
        AssertNoCurveCrossesACard(layout, model);
        AssertNoTwoArrowsRunOnOneStroke(layout);
        ArrowPairsDrawnAcrossOneAnother(layout, model).Should().BeEmpty(
            "every arrow of a cycle is the short chord between neighbours, so none of them is drawn across another");
    }

    [Fact]
    public void A_catalog_of_two_hubs_draws_its_relations_without_crossing_a_card()
    {
        // The reported picture: every satellite names the hub and some of them name the first satellite as
        // well, so two tables hold the catalog's relations between them. Whatever the arrangement, the
        // promises are the same: no arrow across a card it does not join, no two arrows on one stroke, and
        // the crossings the search could not remove kept to the few the fan itself forces.
        var hub = CatalogGraphFixture.TableId("two", "hub");
        var first = CatalogGraphFixture.TableId("two", "s00");
        var fixture = CatalogGraphFixture.Create().Table("two", "hub", 1_000, Column("id"));

        for (var satellite = 0; satellite < 24; satellite++)
        {
            fixture.Table("two", $"s{satellite:00}", 100 + satellite, Column("hub_id"));
        }

        for (var satellite = 0; satellite < 24; satellite++)
        {
            var table = CatalogGraphFixture.TableId("two", $"s{satellite:00}");
            fixture.Edge(table, hub, $"fk_s{satellite:00}", ["hub_id"]);

            if (satellite % 4 == 1)
            {
                fixture.Edge(table, first, $"fk_s{satellite:00}_first", ["first_id"]);
            }
        }

        var model = Analyze(fixture);
        var layout = Picture(model, TimeSpan.FromMinutes(5));

        AssertNoCurveCrossesACard(layout, model);
        AssertNoTwoArrowsRunOnOneStroke(layout);

        // What is asserted here is the arrangement the search finishes with, not the one the clock leaves: this
        // catalog spends more than the ten-second limit when it is searched in full, so asking for it without
        // one would compare a degraded picture against a reading promise only the finished search keeps. The
        // degraded path promises the hard rules instead — every card placed, every relation routed and clear of
        // the cards it does not join — and `A_watchdog_that_fires_returns_a_complete_layout_and_says_it_is_
        // degraded` is where that is pinned.
    }

    [Fact]
    public void A_catalog_that_is_several_pictures_draws_them_apart()
    {
        // A database is rarely one picture: a family gathered around one table and a chain that joins
        // nothing of it are two pictures. The grouping is read off the constraints, each picture is arranged
        // on its own, and the boxes are packed side by side — so no card of one stands inside another's
        // picture, where an arrow would have to bow around a table with no part in it.
        var assembly = CatalogGraphFixture.TableId("parts", "assembly");
        var fixture = CatalogGraphFixture.Create().Table("parts", "assembly", 1_000, Column("id"));

        for (var step = 0; step < 5; step++)
        {
            fixture.Table("parts", $"part{step}", 100 + step, Column("assembly_id"));
            fixture.Edge(CatalogGraphFixture.TableId("parts", $"part{step}"), assembly,
                $"fk_part_{step}", ["assembly_id"]);
        }

        fixture.Table("notes", "top", 40, Column("id"));
        for (var step = 0; step < 4; step++)
        {
            fixture.Table("notes", $"under{step}", 60 + step, Column("prev_id"));
            fixture.Edge(
                CatalogGraphFixture.TableId("notes", $"under{step}"),
                CatalogGraphFixture.TableId("notes", step == 0 ? "top" : $"under{step - 1}"),
                $"fk_under_{step}",
                ["prev_id"]);
        }

        var model = Analyze(fixture);
        var layout = Picture(model);

        var family = model.Nodes.Where(node => node.Schema == "parts").Select(node => layout.Nodes[node.Index]);
        var other = model.Nodes.Where(node => node.Schema == "notes").Select(node => layout.Nodes[node.Index]);
        var (one, two) = (Box(family), Box(other));

        (one.Right <= two.Left || two.Right <= one.Left || one.Bottom <= two.Top || two.Bottom <= one.Top)
            .Should().BeTrue("two pictures that share no constraint are stood apart instead of one inside the other");

        ArrowPairsDrawnAcrossOneAnother(layout, model).Should().BeEmpty(
            "no arrow is drawn across another, whatever the arrangement the engine kept");
    }

    private static (double Left, double Top, double Right, double Bottom) Box(IEnumerable<NodePlacement> cards) =>
        (cards.Min(card => card.TopLeft.X), cards.Min(card => card.TopLeft.Y),
            cards.Max(card => card.Right), cards.Max(card => card.Bottom));

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
        var layout = Picture(model);

        var hub = model.Nodes.Select((node, index) => (node, index)).Single(pair => pair.node.Table == "ledger").index;
        var ports = GraphPortAssigner.Assign(
            layout.Nodes,
            model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());

        ports.IsComplete.Should().BeTrue();

        var ends = model.Edges
            .SelectMany(edge => new[]
            {
                (Node: edge.ChildIndex, Port: ports.Ends[edge.Index]!.Child),
                (Node: edge.ParentIndex, Port: ports.Ends[edge.Index]!.Parent),
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
                        GraphGeometryContract.MinimumPortStep,
                        $"two of the twenty ends on the {side.Key} side stand closer together than a stroke apart");
                }
            }
        }

        AssertNoTwoArrowsRunOnOneStroke(layout);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Whether two cards share a stretch of the page across, which is how one stands under another.</summary>
    private static bool Overlaps(NodePlacement one, NodePlacement two) =>
        one.TopLeft.X < two.Right && two.TopLeft.X < one.Right;

    /// <summary>Whether one card stands under another and within its stretch of the page.</summary>
    private static bool Under(NodePlacement child, NodePlacement parent) =>
        child.TopLeft.Y > parent.TopLeft.Y && Overlaps(child, parent);

    /// <summary>The unit vector a border of a card faces away by.</summary>
    private static (double X, double Y) Facing(EEdgeSide side) => side switch
    {
        EEdgeSide.Top => (0, -1),
        EEdgeSide.Bottom => (0, 1),
        EEdgeSide.Left => (-1, 0),
        _ => (1, 0),
    };

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

    /// <summary>
    /// Asserts no card of the picture is drawn on another. Rings size a ring for the cards and the air it
    /// holds, so a stack of two cards is an arrangement bug and not a routing one: the two names stand in
    /// one place and the reader cannot say which card an arrow ends on.
    /// </summary>
    private static void AssertNoCardsOverlap(CatalogGraphLayout layout)
    {
        var stacked = new List<string>();

        for (var first = 0; first < layout.Nodes.Count; first++)
        {
            for (var second = first + 1; second < layout.Nodes.Count; second++)
            {
                var one = layout.Nodes[first];
                var two = layout.Nodes[second];
                if (one.TopLeft.X < two.Right && two.TopLeft.X < one.Right
                    && one.TopLeft.Y < two.Bottom && two.TopLeft.Y < one.Bottom)
                {
                    stacked.Add($"cards {one.NodeIndex} and {two.NodeIndex}");
                }
            }
        }

        stacked.Should().BeEmpty("no card may be drawn on another card");
    }

    /// <summary>
    /// Walks every curve and asserts it never passes through a card it does not join: each stretch of the
    /// stroke the document paints is sampled, on the same flattened centerline the layout's own hard checks
    /// read. A straight relation is exempt — the cards are painted over the ink, so a chord running behind a
    /// table is the picture the catalog draws, while a curve is hidden by nothing and may not enter a card.
    /// Violations are collected, so one run names every offending route instead of only the first.
    /// </summary>
    private static void AssertNoCurveCrossesACard(CatalogGraphLayout layout, CatalogGraphModel model)
    {
        var violations = layout.Edges
            .Where(route => route.Geometry != EEdgeGeometry.Straight)
            .SelectMany(route => CardsUnder(layout, model, route)
                .Distinct()
                .Select(card => Violation(model.Edges[route.EdgeIndex], route, card)));

        violations.Distinct().Should().BeEmpty("a curve is hidden by nothing, so it may not enter a card it does not join");
    }

    /// <summary>The cards a route's ink enters, the two its own relation joins left out.</summary>
    private static IReadOnlyList<int> CardsUnder(CatalogGraphLayout layout, CatalogGraphModel model, EdgeRoute route)
    {
        var edge = model.Edges[route.EdgeIndex];
        var others = layout.Nodes
            .Where(node => node.NodeIndex != edge.ChildIndex && node.NodeIndex != edge.ParentIndex)
            .ToList();
        var crossed = new List<int>();

        var points = Stroke(route);
        for (var index = 0; index + 1 < points.Count; index++)
        {
            var from = points[index];
            var to = points[index + 1];
            var (dx, dy) = (to.X - from.X, to.Y - from.Y);
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((dx * dx) + (dy * dy))));

            for (var step = 0; step <= steps; step++)
            {
                var share = (double)step / steps;
                var point = new GraphPoint(from.X + (dx * share), from.Y + (dy * share));

                crossed.AddRange(others.Where(card => GraphGeometry.Contains(card, point)).Select(card => card.NodeIndex));
            }
        }

        return crossed;
    }

    /// <summary>
    /// The centerline the document paints for a route: a straight stretch as its two points, a cubic or a
    /// loop through the same adaptive flattening the layout's hard checks read, so a curve with more than
    /// one segment is walked as the page draws it.
    /// </summary>
    private static IReadOnlyList<GraphPoint> Stroke(EdgeRoute route) =>
        route.Geometry is EEdgeGeometry.Cubic or EEdgeGeometry.Loop
            ? GraphGeometry.Flatten(route.Points).Points
            : route.Points;

    private static string Violation(CatalogGraphEdge edge, EdgeRoute route, int card) =>
        $"edge {edge.Index} ({edge.ChildIndex} -> {edge.ParentIndex}) crosses table {card}: " +
        route.Shape + " " + string.Join(' ', route.Points.Select(point => $"({Round(point.X)},{Round(point.Y)})"));

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
    /// Every curve read as the ink it draws, then every pair compared: two stretches are one stroke when
    /// they head the same way and lie within a mark's gap of one another over a stretch worth drawing —
    /// the case a reader cannot read apart, however the router arrived at it. A crossing is one point of
    /// contact and stays two arrows, so the run has to continue in both directions before it counts.
    /// Straight relations are read apart at their ends, which sit a port step apart on their own cards'
    /// borders, and rejoin only behind the cards they pass; a curve has nothing but its ink between it and
    /// its neighbour, so the rule is about curves.
    /// </summary>
    private static void AssertNoTwoArrowsRunOnOneStroke(CatalogGraphLayout layout)
    {
        var traces = layout.Edges.Where(route => route.Geometry != EEdgeGeometry.Straight)
            .Select(route => (route.EdgeIndex, Trace: Trace(route))).ToList();
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

        violations.Distinct().Should().BeEmpty("two curves drawn on one line read as one arrow joining four tables");
    }

    /// <summary>A route written out for a failure message: its shape and every point the document paints.</summary>
    private static string Show(EdgeRoute route) =>
        route.Shape + " " + string.Join(' ', route.Points.Select(point => $"({Round(point.X)},{Round(point.Y)})"));

    /// <summary>
    /// The pairs of arrows drawn across one another. Every pair that joins no table in common is compared
    /// stretch against stretch by the one rule the chord router bows a way to avoid, so what is counted here
    /// and what the router avoids cannot drift apart. Two arrows that meet on a card's border are left out:
    /// those belong to the same table by the picture's own design.
    /// </summary>
    private static List<(int First, int Second)> ArrowPairsDrawnAcrossOneAnother(
        CatalogGraphLayout layout,
        CatalogGraphModel model)
    {
        var traces = layout.Edges.Select(route => Trace(route).Points).ToList();
        var crossed = new List<(int, int)>();

        for (var first = 0; first < traces.Count; first++)
        {
            for (var second = first + 1; second < traces.Count; second++)
            {
                if (!SharesATable(model.Edges[first], model.Edges[second]) && MeetsInTheOpen(traces[first], traces[second]))
                {
                    crossed.Add((first, second));
                }
            }
        }

        return crossed;
    }

    private static bool SharesATable(CatalogGraphEdge a, CatalogGraphEdge b) =>
        a.ChildIndex == b.ChildIndex || a.ChildIndex == b.ParentIndex
        || a.ParentIndex == b.ChildIndex || a.ParentIndex == b.ParentIndex;

    private static bool MeetsInTheOpen(IReadOnlyList<GraphPoint> one, IReadOnlyList<GraphPoint> other)
    {
        for (var first = 0; first + 1 < one.Count; first++)
        {
            for (var second = 0; second + 1 < other.Count; second++)
            {
                if (GraphGeometry.SegmentsCross(one[first], one[first + 1], other[second], other[second + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A route as the paper shows it: points along the stroke, and how far along the stroke each sits.</summary>
    private sealed record InkTrace(IReadOnlyList<GraphPoint> Points, IReadOnlyList<double> Along);

    /// <summary>A route walked at a step small enough to compare two strokes as the eye follows them.</summary>
    private static InkTrace Trace(EdgeRoute route)
    {
        var shape = Stroke(route);

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

            if (near.Distance > GraphGeometryContract.MinimumCenterlineDistance || !Aligned(one, index, other, near.Index))
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
    /// Whether two stretches are drawn the same way at one point. Two strokes that head the same way by
    /// less than the least angle the picture draws a crossing at are read as one line, and the test names
    /// the contract's constant rather than a number of its own — a stroke crossing another at a wider
    /// angle is read as a crossing, and the eye is right to read it that way.
    /// </summary>
    private static bool Aligned(InkTrace one, int at, InkTrace other, int nearest)
    {
        var (ux, uy) = Step(one, at);
        var (vx, vy) = Step(other, nearest);
        var lengths = Math.Sqrt((ux * ux) + (uy * uy)) * Math.Sqrt((vx * vx) + (vy * vy));

        return lengths > 0
            && Math.Asin(Math.Clamp(Math.Abs((ux * vy) - (uy * vx)) / lengths, 0, 1))
                < GraphGeometryContract.MinimumCrossingAngle;
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
