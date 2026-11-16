using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbClone.Application.Interchange;
using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services;
using DbClone.UI.Services.GraphLayout;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Locks the document's contract: it is complete, it is self-contained, no catalog string becomes
/// markup, and the same catalog produces the same bytes twice.
/// </summary>
public sealed class CatalogGraphHtmlRendererTests
{
    private static readonly DateTimeOffset Moment = new(2026, 11, 10, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_document_needs_nothing_from_outside_itself()
    {
        // Arrange
        var html = SmallCatalog().Document;

        // Assert
        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().NotContain("http://").And.NotContain("https://");
        html.Should().NotContain(" src=").And.NotContain("<link").And.NotContain("@import");
        html.Should().Contain("<style>").And.Contain("<svg");
        Count(html, "<style").Should().Be(1);
        Count(html, "<script").Should().Be(2);
        Count(html, "type=\"application/json\"").Should().Be(1);
    }

    [Fact]
    public void A_table_name_that_looks_like_markup_stays_text()
    {
        // Arrange
        var hostile = "</script><img src=x onerror=\"alert(1)\">";
        var fixture = CatalogGraphFixture.Create()
            .Table("public", "orders")
            .Table("public", hostile)
            .Edge(CatalogGraphFixture.TableId("public", hostile), CatalogGraphFixture.TableId("public", "orders"));
        var rendered = SmallCatalog(fixture);

        // Assert
        rendered.Document.Should().NotContain("</script><img");
        rendered.Document.Should().Contain("&lt;/script&gt;");
        rendered.Island.Should().NotContain("<").And.NotContain(">");
        JsonDocument.Parse(rendered.Island).RootElement.GetProperty("nodes").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Every_node_and_every_edge_is_drawn_exactly_once()
    {
        // Arrange
        var rendered = SmallCatalog();

        // Assert
        for (var node = 0; node < rendered.Model.Nodes.Count; node++)
        {
            Count(rendered.Document, $"data-n=\"{node}\"").Should().Be(1);
        }

        for (var edge = 0; edge < rendered.Model.Edges.Count; edge++)
        {
            Count(rendered.Document, $"<path class=\"edge\" data-e=\"{edge}\"").Should().Be(1);
            Count(rendered.Document, $"<tr data-e=\"{edge}\"").Should().Be(1);
        }
    }

    [Fact]
    public void The_register_prints_every_constraint_in_full()
    {
        // Arrange
        var rendered = Render(Chain(70));

        // Assert
        rendered.Document.Should().Contain("<th>constraint</th>").And.Contain("<th>on delete</th>")
            .And.Contain("<th>rule</th>");
        Count(rendered.Document, "<tr data-e=").Should().Be(rendered.Model.Edges.Count);
        rendered.Document.Should().Contain("customer_id → id");
        rendered.Document.Should().Contain("cascade");
    }

    [Fact]
    public void A_withheld_label_is_still_in_the_document_and_reveals_on_hover_or_selection()
    {
        // Arrange
        var rendered = Render(Chain(70));

        // Assert
        rendered.Document.Should().Contain("class=\"elabel quiet\"");
        rendered.Document.Should().Contain("path.edge:not(.off).hot+text.elabel");
        rendered.Document.Should().Contain("path.edge.pick:not(.off):hover+text.elabel");
        Count(rendered.Document, "class=\"elabel").Should().Be(rendered.Model.Edges.Count);
    }

    [Fact]
    public void A_cluster_switched_off_takes_its_supernode_with_it()
    {
        // Arrange
        var rendered = Render(Ring(9));

        // Assert — a table is claimed by one chip per axis and each axis filters on its own, so a schema
        // the reader switches off hides the grouped tables it also claims, and the box drawn over a
        // collapsed group goes with its members instead of naming tables that are no longer on screen.
        rendered.Document.Should().Contain("axisOn(claims, \"Schema\")")
            .And.Contain("axisOn(claims, \"CyclicGroup\")")
            .And.Contain("axisOn(claims, \"Unconnected\")");
        rendered.Document.Should().Contain(".super.off");
    }

    [Fact]
    public void The_picture_opens_on_itself_rather_than_on_the_canvas_corner()
    {
        // Arrange
        var rendered = Render(Chain(12));

        // Assert — the stage takes the height the window has left instead of a guessed offset, and both
        // the opening view and "100%" centre the visible cards in it rather than moving the canvas
        // origin into the stage's corner.
        rendered.Document.Should().Contain("function placeContent(wanted)")
            .And.Contain("if (action === \"reset\") placeContent(1);");
        rendered.Document.Should().NotContain("calc(100vh");

        // The place is taken again once the document has been laid out and on every resize, and a stage
        // that measured nothing refuses the placement: a camera read before the layout landed made every
        // axis look too wide for the stage and parked a picture that fits in its corner, beside blank
        // stage. No caller can reach that state now, because the refusal is inside placeContent itself.
        rendered.Document.Should().Contain("if (stage.width <= 0 || stage.height <= 0) return false;")
            .And.Contain("window.addEventListener(\"load\", keepCentred, { once: true });")
            .And.Contain("window.addEventListener(\"resize\", keepCentred);")
            .And.Contain("new ResizeObserver(keepCentred)");

        // A picture larger than the stage is aligned to its own top-left corner instead of centred, so
        // "100%" lands on the first tables rather than on the middle of the canvas.
        rendered.Document.Should().Contain("width <= stage.width")
            .And.Contain("height <= stage.height");
    }

    [Fact]
    public void The_picture_fills_the_stage_rather_than_the_canvas()
    {
        // The reader's report: four table cards sliced at a straight vertical line with untouched white
        // beyond it. The svg is the one box the browser clips to, so sizing it to the layout canvas made
        // its own edge a cut — the camera centres and pans in the stage, and a picture moved past the
        // canvas width painted nothing at all.
        var rendered = Render(Chain(12));

        // Assert — the element fills the stage and names no size of its own.
        rendered.Document.Should().Contain("#pic{position:absolute;top:0;left:0;width:100%;height:100%;");
        rendered.Document.Should().Contain("<svg id=\"pic\" role=\"img\"")
            .And.NotContain("<svg id=\"pic\" width=");
    }

    [Fact]
    public void A_picture_drawn_again_opens_where_the_reader_left_the_one_it_replaces()
    {
        // The reader's report: switching a cluster off threw the picture back to its opening position,
        // so the table being looked at was lost to a filter that says nothing about where the reader is.
        // The camera is stated by the document, carried by the viewer into the address it serves next,
        // and applied on open instead of placing the picture at its own size.
        var rendered = Render(Chain(12));

        // Assert
        rendered.Document.Should().Contain("window.dbcloneCamera = function ()");
        rendered.Document.Should().Contain("function openingCamera()");
        rendered.Document.Should().Contain("if (opening) {").And.Contain("applyCamera(opening);");
        rendered.Document.Should().NotContain("else placeContent(1);");
    }

    [Fact]
    public void The_folded_group_box_answers_the_keyboard_it_offers()
    {
        // Arrange — the box carries role="button" and tabindex="0", so Tab reaches it; a control the
        // reader can reach and then cannot press is a button that does not work.
        var rendered = Render(Ring(9));

        // Assert
        rendered.Document.Should().Contain("svg.addEventListener(\"keydown\"");
        rendered.Document.Should().Contain("event.key !== \"Enter\"");
    }

    [Fact]
    public void Selecting_a_search_hit_can_show_the_card_it_names()
    {
        // Arrange
        var rendered = Render(Chain(12));

        // Assert — revealing a hit asks the picture for a table's card while the island's own lookup is
        // a map. One name for both would leave the map where the reveal calls the function: the call
        // throws, and a search that reports a match then shows nothing at all.
        rendered.Document.Should().Contain("function cardElement(index)");
        rendered.Document.Should().Contain("var card = cardElement(index);");
        rendered.Document.Should().NotContain("function cardOf(");
    }

    [Fact]
    public void Every_arrow_is_pointed_at_from_beside_its_line()
    {
        // Arrange
        var rendered = SmallCatalog();

        // Assert — the line is 1.3 px of ink, which is a hard thing to hit, so a second path with the same
        // geometry lies over it and carries the pointer with a 10 px transparent stroke. It repeats the
        // edge's own data-e, so the highlight, the direction filter and the click handler treat the two as
        // one arrow, and it is written immediately before the label, which is what lets the label's reveal
        // follow the band the reader points at rather than the stroke they cannot land on (FR13).
        Count(rendered.Document, "class=\"edge pick\"").Should().Be(rendered.Model.Edges.Count);
        rendered.Document.Should().Contain(".edge.pick{stroke:transparent;stroke-width:10px");
        rendered.Document.Should().Contain("</path>\n<text class=\"elabel quiet\"");

        // The count line walks the same list, so it must skip the band: two paths per arrow would
        // otherwise state every relation twice.
        rendered.Document.Should().Contain("!element2.classList.contains(\"pick\")");
    }

    [Fact]
    public void No_label_is_printed_on_the_picture()
    {
        // Arrange — two tables and one constraint: even with room to spare the corridor carries the
        // line and nothing else, because text written along the arrows is what made the picture hard
        // to read, and a label that appears only where it fits teaches the reader to distrust the
        // silence of the ones that do not.
        var rendered = SmallCatalog();

        // Assert
        rendered.Model.Edges.Should().ContainSingle();
        rendered.Document.Should().Contain("<text class=\"elabel quiet\"");
        rendered.Document.Should().NotContain("<text class=\"elabel\" ");
        rendered.Document.Should().Contain(">customer_id → id</text>");
    }

    [Fact]
    public void The_script_never_turns_a_string_into_markup()
    {
        // Arrange
        var html = Render(Chain(3)).Document;

        // Assert
        html.Should().NotContain("innerHTML");
        html.Should().NotContain("outerHTML");
        html.Should().NotContain("insertAdjacentHTML");
        html.Should().NotContain("document.write");
        html.Should().NotContain("eval(");
    }

    [Fact]
    public void The_same_catalog_renders_the_same_bytes()
    {
        // Arrange
        var fixture = Chain(24);

        // Act
        var first = Render(fixture).Document;
        var second = Render(fixture).Document;

        // Assert
        second.Should().Be(first);
    }

    [Fact]
    public void The_count_line_states_the_whole_catalog_before_any_filtering()
    {
        // Arrange
        var rendered = SmallCatalog();

        // Assert
        rendered.Document.Should()
            .Contain($"Showing {rendered.Model.TableCount} of {rendered.Model.TableCount} tables · " +
                $"{rendered.Model.RelationCount} of {rendered.Model.RelationCount} relations");
    }

    [Fact]
    public void Every_chip_a_reader_can_switch_is_a_button_that_names_the_tables_it_claims()
    {
        // Arrange
        var fixture = CatalogGraphFixture.Create()
            .Table("public", "orders")
            .Table("public", "customers")
            .Table("sales", "lonely")
            .Edge(CatalogGraphFixture.TableId("public", "orders"), CatalogGraphFixture.TableId("public", "customers"));
        var rendered = Render(fixture);

        // Assert
        Count(rendered.Document, "class=\"chip on\"").Should().Be(rendered.Model.Chips.Count);
        foreach (var chip in rendered.Model.Chips)
        {
            rendered.Document.Should().Contain($"data-c=\"{chip.Index}\"");
        }

        rendered.Document.Should().Contain("<span class=\"hint\">", "two schemas make the isolating gesture worth naming");
        rendered.Document.Should().NotContain("<span>unconnected</span>",
            "the chip already says that word, and the bar may not say it twice");

        var lonely = rendered.Model.NodeIndexesById[CatalogGraphFixture.TableId("sales", "lonely")];
        var claims = string.Join(' ', rendered.Model.ChipIndexesByNode[lonely]);
        claims.Should().EndWith(UnconnectedChip(rendered.Model), "the unconnected chip claims it");
        rendered.Document.Should().Contain($"data-n=\"{lonely}\" data-c=\"{claims}\"");
    }

    [Fact]
    public void A_catalog_with_one_schema_states_it_once_instead_of_offering_a_switch()
    {
        // Arrange
        var fixture = CatalogGraphFixture.Create()
            .Table("public", "orders")
            .Table("public", "customers")
            .Table("public", "lonely")
            .Edge(CatalogGraphFixture.TableId("public", "orders"), CatalogGraphFixture.TableId("public", "customers"));
        var rendered = Render(fixture);

        // Assert
        var schemaChip = rendered.Model.Chips.Should().ContainSingle(chip => chip.Kind == EClusterChipKind.Schema).Subject;
        rendered.Document.Should().NotContain($"class=\"chip on\" data-c=\"{schemaChip.Index}\"",
            "a lone schema chip can only hide every table at once");
        rendered.Document.Should().Contain("<span>schema</span>");
        rendered.Document.Should().Contain("<b>public</b>");
        rendered.Document.Should().NotContain("class=\"hint\"");

        var lonely = rendered.Model.NodeIndexesById[CatalogGraphFixture.TableId("public", "lonely")];
        var claims = string.Join(' ', rendered.Model.ChipIndexesByNode[lonely]);
        claims.Should().Contain(schemaChip.Index.ToString(), "the analysis keeps the claim");
        rendered.Document.Should().Contain($"data-n=\"{lonely}\" data-c=\"{UnconnectedChip(rendered.Model)}\"",
            "a stated schema must not claim, or the unconnected switch would change nothing");
    }

    [Fact]
    public void The_name_and_the_count_are_printed_inside_the_card()
    {
        // Arrange
        var rendered = SmallCatalog();
        var node = rendered.Document[rendered.Document.IndexOf("<g class=\"node", StringComparison.Ordinal)..];

        // Assert
        rendered.Document.Should().NotContain("class=\"rows\"", "the rectangle carries no number of its own");
        Count(rendered.Document, "class=\"count\"").Should().Be(2);
        rendered.Document.Should().Contain(">1,200 rows<").And.Contain(">40 rows<");

        var box = Regex.Match(node, "<rect x=\"(?<x>[0-9.]+)\" y=\"(?<y>[0-9.]+)\" width=\"(?<w>[0-9.]+)\" height=\"(?<h>[0-9.]+)\"");
        var name = Regex.Match(node, "<text class=\"name\" x=\"(?<x>[0-9.]+)\" y=\"(?<y>[0-9.]+)\">(?<text>[^<]*)</text>");
        var count = Regex.Match(node, "<text class=\"count\" x=\"(?<x>[0-9.]+)\" y=\"(?<y>[0-9.]+)\">(?<text>[^<]*)</text>");

        box.Success.Should().BeTrue("a table is drawn as a rectangle");
        name.Success.Should().BeTrue("the table's name is on the picture");
        count.Success.Should().BeTrue("the row count is on the picture");

        var (left, top, width, height) = (Number(box, "x"), Number(box, "y"), Number(box, "w"), Number(box, "h"));

        Number(name, "x").Should().BeGreaterThan(left, "the name is inside the card, not beside it");
        Number(count, "x").Should().Be(Number(name, "x"), "both lines start on the same edge");
        Number(name, "y").Should().BeInRange(top, top + height);
        Number(count, "y").Should().BeGreaterThan(Number(name, "y")).And.BeLessThan(top + height);
        height.Should().Be(GraphCard.Height);
        width.Should().BeApproximately(
            GraphCard.Width(name.Groups["text"].Value, count.Groups["text"].Value),
            0.01,
            "the card is measured by the two lines it prints");
    }

    [Fact]
    public void A_cyclic_group_opens_as_one_supernode_over_hidden_members()
    {
        // Arrange
        var rendered = Render(Ring(9));

        // Assert
        var group = rendered.Model.CyclicGroups.Should().ContainSingle().Subject;
        group.IsCollapsed.Should().BeTrue();
        rendered.Document.Should().Contain($"<g class=\"super\" data-g=\"{group.Number}\"");
        rendered.Document.Should().Contain("↺ 9");
        Count(rendered.Document, $"data-m=\"{group.Number}\"").Should().Be(9);
        foreach (var member in group.MemberNodeIndexes)
        {
            Count(rendered.Document, $"data-n=\"{member}\" data-c=").Should().Be(1);
        }

        rendered.Document.Should().Contain(" member hidden");
    }

    [Fact]
    public void A_catalog_the_whole_rule_holds_opens_with_nothing_hidden()
    {
        // Arrange
        var rendered = Render(Chain(24));

        // Assert
        rendered.Model.InitialView.ShowsWholeGraph.Should().BeTrue();
        rendered.Document.Should().NotContain(" away");
    }

    [Fact]
    public void A_search_hit_reveals_the_table_it_names()
    {
        // The reader's report: a search for a table the picture was not showing selected a card that
        // stayed display:none, so nothing in the picture changed and the search read as broken. A card
        // is hidden three ways — outside the neighbourhood the picture opened on (`away`), a chip the
        // reader switched off (`off`), a collapsed cyclic group (`hidden`) — and a hit has to undo the
        // one it meets before it selects the card (FR13, D8).
        var rendered = Render(Chain(70));

        rendered.Model.InitialView.ShowsWholeGraph.Should().BeFalse("the fixture opens on one neighbourhood");
        rendered.Document.Should().Contain("class=\"node cap away\"");
        rendered.Model.SearchIndex.Should().Contain(
            entry => entry.Text == "t69",
            "a search covers the whole catalog, not only what is on screen");

        rendered.Document.Should().Contain("showCard(Number(row.getAttribute(\"data-n\")))");
        rendered.Document.Should().Contain("card.classList.remove(\"away\")");
        rendered.Document.Should().Contain("setChip(chip, true)");
        rendered.Document.Should().Contain("toggleGroup(Number(group))");
        rendered.Document.Should().Contain("focusNode(index)");

        // A card taken to a place nobody can see is still a search that appears to do nothing, so the
        // picture is moved to the card, and the arrows it joins to tables already on screen come back
        // with it while the count line restates what the picture now shows (D7, D8a).
        rendered.Document.Should().Contain("bringIntoView(index)");
        rendered.Document.Should().Contain("if (neighbour && shown(neighbour)) element2.classList.remove(\"away\")");
        rendered.Document.Should().Contain("!element2.classList.contains(\"away\")");
        rendered.Document.Should().Contain("\\u2014 filtered");
    }

    [Fact]
    public void An_arrow_to_a_table_the_picture_is_not_showing_is_not_drawn()
    {
        // The same opening view: an arrow was drawn to a position where no card stands, so the picture
        // pointed at nothing and the count line claimed the whole catalog. An arrow whose table is not
        // drawn now carries that table's own `away` class, and is revealed with it (D8a).
        var rendered = Render(Chain(70));
        var shown = rendered.Model.InitialView.VisibleNodeIndexes;

        // Assert
        Count(rendered.Document, "class=\"edge away\" data-e").Should().Be(
            rendered.Model.Edges.Count(edge => !shown.Contains(edge.ChildIndex) || !shown.Contains(edge.ParentIndex)));
        Count(rendered.Document, "class=\"edge pick away\" data-e").Should().Be(
            Count(rendered.Document, "class=\"edge away\" data-e"),
            "the band that takes the pointer is hidden with the line it covers");
        rendered.Document.Should().Contain("edges.forEach(function (element2) { element2.classList.remove(\"away\"); });");
    }

    [Fact]
    public void The_panel_states_the_table_it_names_instead_of_pointing_at_the_catalog_files()
    {
        // The reader's report: selecting a table ended in "full definitions, including index DDL, are
        // in the catalog at schema/tables.json", which asks the reader to leave the document and read
        // the folder's JSON by hand. The panel states the key, the unique constraints, the indexes by
        // the columns they cover, and the check constraints itself (D10).
        var rendered = SmallCatalog();
        var island = JsonDocument.Parse(rendered.Island).RootElement;
        var orders = island.GetProperty("details").EnumerateArray()
            .Single(detail => detail.GetProperty("name").GetString() == "orders");

        // Assert — every fact the panel prints came from the catalog, not from a sentence about it.
        orders.GetProperty("pk").GetProperty("name").GetString().Should().Be("orders_pkey");
        orders.GetProperty("pk").GetProperty("cols")[0].GetString().Should().Be("id");
        orders.GetProperty("uq")[0].GetProperty("name").GetString().Should().Be("uq_orders_external");
        orders.GetProperty("uq")[0].GetProperty("cols")[0].GetString().Should().Be("external_ref");
        orders.GetProperty("idx")[0].GetProperty("name").GetString().Should().Be("ix_orders_customer");
        orders.GetProperty("idx")[0].GetProperty("cols")[0].GetString().Should()
            .Be("customer_id", "an index is stated by the columns it covers");
        orders.GetProperty("idx")[0].GetProperty("def").GetString()
            .Should().Be("CREATE INDEX ix_orders_customer ON public.orders USING btree (customer_id)",
                "the folder's own statement is carried, so a catalog that states no columns can still print it");
        orders.GetProperty("ck")[0].GetProperty("text").GetString().Should().Be("total >= 0");

        rendered.Document.Should().Contain("definitionList(detail)");
        rendered.Document.Should().Contain("\"Definition (\"");
        rendered.Document.Should().NotContain("Full definitions");
        rendered.Document.Should().NotContain("schema/tables.json");
    }

    [Fact]
    public void An_index_prints_its_columns_and_falls_back_to_the_captured_text_when_none_are_stated()
    {
        // A reader wants to know which columns an index covers, not to read a SQL statement to find out.
        // The catalog states the columns as data, so the panel prints them and keeps the engine's text
        // out of the way. A folder captured before the columns were recorded states none, and there the
        // text is the only surviving record of the key, so it is printed rather than nothing.
        var rendered = Render(CatalogGraphFixture.Create()
            .Table("public", "orders", 12, Column("id", "bigint", isPrimaryKey: true), Column("customer_id", "bigint"))
                .Index(
                    "ix_orders_customer",
                    definition: "CREATE INDEX ix_orders_customer ON public.orders USING btree (customer_id)",
                    columns: ["customer_id"])
                .Index("ix_orders_old", definition: "CREATE INDEX ix_orders_old ON public.orders USING btree (id)"));
        var orders = JsonDocument.Parse(rendered.Island).RootElement.GetProperty("details").EnumerateArray()
            .Single(detail => detail.GetProperty("name").GetString() == "orders");

        // Assert
        orders.GetProperty("idx")[0].GetProperty("cols")[0].GetString().Should().Be("customer_id");
        orders.GetProperty("idx")[1].TryGetProperty("cols", out var unstated).Should().BeFalse(
            "a folder that stated no columns writes none, which is not the same as an index over nothing");
        orders.GetProperty("idx")[1].GetProperty("def").GetString()
            .Should().Be("CREATE INDEX ix_orders_old ON public.orders USING btree (id)");

        // The row: columns when they are known, and the captured text only when they are not.
        rendered.Document.Should().Contain(
            "known ? \"index (\" + index.cols.join(\", \") + \")\" : \"index\"",
            "an index is named by the columns it covers");
        rendered.Document.Should().Contain(
            "text: known ? null : index.def",
            "the engine's statement is shown only where the catalog states no key");
    }

    [Fact]
    public void Two_constraints_joining_the_same_columns_are_told_apart_by_their_names()
    {
        // The reader's report: the panel printed the same line twice. The label joins the child's
        // columns to the parent's, so two constraints on one column pair read identically — only the
        // constraint's name and its rule tell them apart, and both are printed on the row.
        var lines = CatalogGraphFixture.TableId("public", "order_lines");
        var products = CatalogGraphFixture.TableId("public", "products");
        var rendered = Render(CatalogGraphFixture.Create()
            .Table("public", "products", 10, Column("id", "bigint", isPrimaryKey: true))
            .Table("public", "order_lines", 90, Column("product_id", "bigint"))
            .Edge(lines, products, "fk_lines_products", ["product_id"], ["id"], mandatory: true)
            .Edge(lines, products, "fk_lines_products_alt", ["product_id"], ["id"]));

        var labels = JsonDocument.Parse(rendered.Island).RootElement.GetProperty("edges")
            .EnumerateArray()
            .Select(edge => edge.GetProperty("label").GetString())
            .ToList();

        // Assert — the two labels are the same text, which is exactly why the row may not stop there.
        labels.Should().HaveCount(2);
        labels[0].Should().Be(labels[1], "the label names the columns, and both constraints join the same ones");
        rendered.Island.Should().Contain("fk_lines_products_alt");
        rendered.Document.Should().Contain("button.appendChild(element(\"b\", \"mono\", edge.name || \"(unnamed constraint)\"))");
        rendered.Document.Should().Contain("button.appendChild(element(\"span\", \"mono\", columnText(edge)))");
        rendered.Document.Should().Contain("button.appendChild(element(\"span\", \"note\", ruleText(edge)))");
        rendered.Document.Should().Contain("the child row cannot lose its parent");
        rendered.Document.Should().Contain("the child row may have no parent");
    }

    [Fact]
    public void One_to_one_is_marked_when_a_key_of_the_child_covers_its_columns_exactly()
    {
        // The reader asked why every arrow meant many-to-one. A foreign key always references a unique
        // key, so the parent end is always one; only the child end can say something else, and the
        // catalog proves it only when a key of the child has exactly the referencing columns as its
        // column set (D14.1, D14.2).
        var rendered = Render(Shapes());
        var edges = Edges(rendered.Island);

        var settings = edges.Single(edge => edge.GetProperty("name").GetString() == "fk_settings_customers");
        settings.GetProperty("one").GetBoolean().Should().BeTrue("user_settings is keyed on user_id alone");
        settings.GetProperty("exact").GetBoolean().Should().BeTrue("the child row cannot be absent either");

        var orders = edges.Single(edge => edge.GetProperty("name").GetString() == "fk_orders_customers");
        orders.GetProperty("one").GetBoolean().Should().BeFalse("a key on id says nothing about customer_id");

        rendered.Document.Should().Contain("exactly one child row names each parent");
        rendered.Document.Should().Contain("the child row cannot lose its parent",
            "a constraint the catalog cannot prove keeps the sentence it always had");

        // Containment is not proof: a key on (user_id, tenant) does not cover user_id alone.
        var wide = Render(CatalogGraphFixture.Create()
            .Table("public", "customers", 40, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("customers_pkey", "id")
            .Table("public", "user_settings", 40, Column("user_id", "bigint"), Column("tenant", "bigint"))
                .PrimaryKey("user_settings_pkey", "user_id", "tenant")
            .Edge(
                CatalogGraphFixture.TableId("public", "user_settings"),
                CatalogGraphFixture.TableId("public", "customers"),
                "fk_settings_customers",
                ["user_id"],
                ["id"],
                mandatory: true));

        var wider = Edges(wide.Island).Single();
        wider.GetProperty("one").GetBoolean().Should().BeFalse("the key covers more columns than the constraint does");
        wider.GetProperty("exact").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void A_unique_index_alone_does_not_make_a_constraint_one_to_one()
    {
        // A unique index proves uniqueness over the columns it names, and the catalog now names them,
        // but nothing records its predicate: a partial unique index would be taken for a key over the
        // whole table. The bar is therefore left off even when the columns match, which is the safe
        // direction to be wrong in — a mark that is missing reads as the default, while a bar printed
        // on a pair that is really many-to-one states something the database does not allow (D14.2).
        var rendered = Render(CatalogGraphFixture.Create()
            .Table("public", "customers", 40, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("customers_pkey", "id")
            .Table("public", "user_settings", 40, Column("user_id", "bigint"))
                .Index(
                    "ux_settings_user",
                    unique: true,
                    definition: "CREATE UNIQUE INDEX ux_settings_user ON public.user_settings USING btree (user_id)",
                    columns: ["user_id"])
            .Edge(
                CatalogGraphFixture.TableId("public", "user_settings"),
                CatalogGraphFixture.TableId("public", "customers"),
                "fk_settings_customers",
                ["user_id"],
                ["id"],
                mandatory: true));

        var edge = Edges(rendered.Island).Single();
        edge.GetProperty("one").GetBoolean().Should().BeFalse(
            "the index names its columns here, so the rule is not keyed off a missing list — it is the "
            + "unstated predicate that keeps the bar off");
        Count(Picture(rendered.Document), "marker-start=\"").Should().Be(0,
            "many is the case the picture does not print, so an unproven pair carries no mark at all");

        var settings = JsonDocument.Parse(rendered.Island).RootElement.GetProperty("details").EnumerateArray()
            .Single(detail => detail.GetProperty("name").GetString() == "user_settings");
        settings.GetProperty("idx")[0].GetProperty("unique").GetBoolean()
            .Should().BeTrue("the fact is in the panel by name and by uniqueness, which is what the reader can see");
    }

    [Fact]
    public void A_link_table_is_named_from_its_primary_key_and_its_two_constraints()
    {
        // No engine declares many-to-many: it emerges from a table whose key is exactly the two
        // columns it points through. The name is an inference, so the panel says so (D14.3).
        var rendered = Render(Shapes());
        var joiner = Nodes(rendered.Island).Single(node => node.GetProperty("name").GetString() == "issue_labels");

        joiner.GetProperty("join").GetString().Should().Be("join table of issues and labels");
        rendered.Document.Should().Contain("join table of issues and labels (inferred)",
            "the card's tooltip carries the same words the panel prints");
        rendered.Document.Should().Contain("card.join + \" \\u2014 inferred\"");
        rendered.Document.Should().Contain("panel.appendChild(element(\"h2\", null, titleOf(detail, cardOf.get(index))));");

        // A link table with a surrogate key is not named, and nothing is printed in its place.
        var id = CatalogGraphFixture.TableId("public", "issue_labels");
        var surrogate = Render(CatalogGraphFixture.Create()
            .Table("public", "issues", 90, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("issues_pkey", "id")
            .Table("public", "labels", 12, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("labels_pkey", "id")
            .Table("public", "issue_labels", 500, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("issue_labels_pkey", "id")
            .Edge(id, CatalogGraphFixture.TableId("public", "issues"), "fk_issue_labels_issue", ["issue_id"], ["id"], mandatory: true)
            .Edge(id, CatalogGraphFixture.TableId("public", "labels"), "fk_issue_labels_label", ["label_id"], ["id"], mandatory: true));

        Nodes(surrogate.Island).Single(node => node.GetProperty("name").GetString() == "issue_labels")
            .TryGetProperty("join", out _).Should().BeFalse("a surrogate key is not the union of the foreign keys");
    }

    [Fact]
    public void Every_arrow_carries_its_mark_on_the_line_and_not_on_the_band()
    {
        // The mark hangs off the painted path, so an arrow that is away, switched off or folded takes
        // it with it. The band that takes the pointer repeats the same geometry, so a mark on it
        // would double every arrow (D14.4). Many-to-one is the shape of nearly every constraint in a
        // catalog, so it is drawn with no mark of its own: a fork printed on every line states the
        // definition of a foreign key over and over while saying nothing about the pair.
        var rendered = Render(Shapes());
        var picture = Picture(rendered.Document);
        var edges = rendered.Model.Edges.Count;
        var one = rendered.Model.Edges.Count(edge => edge.IsOneToOne);

        Count(rendered.Document, "<marker id=\"head\"").Should().Be(1);
        Count(rendered.Document, "<marker id=\"one\"").Should().Be(1, "a definition written twice would drift");
        Count(rendered.Document, "<marker id=\"many\"").Should().Be(0, "the marks live in one defs the key samples too");
        rendered.Document.Should().Contain(
            "<marker id=\"head\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"9\" markerHeight=\"9\"",
            "the head keeps the size it was released with: the room between arrowheads comes from the border, not from a smaller mark");
        rendered.Document.Should().Contain(
            "<marker id=\"one\" viewBox=\"0 0 10 10\" refX=\"0\" refY=\"5\" markerWidth=\"8\" markerHeight=\"8\" orient=\"auto\">"
            + "<path d=\"M 7 0 L 7 10\" stroke=\"#8b949e\" stroke-width=\"2.5\" fill=\"none\"/></marker>",
            "the bar keeps the size and weight it was released with: the room between neighbouring ends comes from the border, not from a smaller mark");

        // One mark per painted line: every arrow is written twice, so a mark on the band would make
        // this count twice the number of one-to-one constraints.
        Count(picture, "marker-start=\"").Should().Be(one);
        Count(picture, "marker-start=\"url(#one)\"").Should().Be(one);
        Count(picture, "marker-end=\"url(#head)\"").Should().Be(edges);
        Count(picture, "class=\"edge pick\"").Should().Be(edges);

        // A mark is an attribute of the painted path, and that fixes the order the writer emits: the
        // path's own data closes first and the marks follow it. A mark written while the data is still
        // open lands inside it — no mark is drawn and the geometry is invalid from there on — so the
        // data is asserted to hold no attribute at all, and a one-to-one arrow to carry its bar between
        // its own data and the head it points with (D2).
        var painted = Regex.Matches(picture, "<path class=\"edge[^>]*>")
            .Select(match => match.Value)
            .Where(tag => !tag.Contains("pick", StringComparison.Ordinal))
            .ToList();

        painted.Should().HaveCount(edges, "one painted path per constraint");

        foreach (var path in painted)
        {
            Regex.Match(path, " d=\"([^\"]*)\"").Groups[1].Value.Should()
                .NotContain("marker-", "the path data closes before any mark is written");
        }

        painted.Should().Contain(
            path => Regex.IsMatch(path, " d=\"[^\"]*\" marker-start=\"url\\(#one\\)\" marker-end=\"url\\(#head\\)\">$"),
            "the bar hangs between the path's own data and the head it points with");
    }

    [Fact]
    public void The_legend_draws_the_shape_it_explains()
    {
        // A reader who meets a mark once needs the key once, and needs to see it rather than build it
        // from a word. Every row samples the shape in the picture's own defs; the two cardinality rows then
        // lead with the relationship name every ER editor shares — many-to-one (N:1), one-to-one (1:1) — and
        // follow it with one plain sentence naming the source and the target table (D5). The bar is a
        // maximum and is never called "exactly one": optionality is decided by a value or a trigger, which
        // the catalog does not carry. The reasoning behind each row is the manual's, not the key's: a
        // paragraph printed beside a 48-pixel sample is a legend nobody reads.
        var rendered = Render(Shapes());

        Count(rendered.Document, "<ul class=\"legend\">").Should().Be(1);
        Count(rendered.Document, "<svg class=\"key\"").Should().Be(7, "one drawn sample per row");

        rendered.Document.Should().Contain(
            "<b>many-to-one (N:1)</b> multiple records in the source table can reference the same record in the target table",
            "the plain arrow leads with the name every ER editor gives it, and explains it in one sentence");
        rendered.Document.Should().Contain(
            "<b>one-to-one (1:1)</b> each record in the target table can be referenced by at most one record in the source table");
        rendered.Document.Should().Contain("<b>dashed</b> the table it points at is not in this catalog");
        rendered.Document.Should().Contain("<b>curve</b> a cycle: the two tables point at each other");
        rendered.Document.Should().Contain("<b>loop</b> the table points at itself");
        rendered.Document.Should().Contain("<b>card</b> one table: its name and row count");
        rendered.Document.Should().Contain("<b>collapsed</b> a cyclic group as one box; select to expand");
        rendered.Document.Should().NotContain("many rows to one row", "the abstract cardinality never said which end it was drawn from");

        // The rows are read back out of the document so the two that name a relationship can be pinned by
        // their label: a description of many-to-one is not the name of it, and a reader of ER diagrams looks
        // for the name.
        var rows = Regex.Matches(rendered.Document, "<b>([^<]+)</b> ([^<]+)</li>")
            .Select(match => (Label: match.Groups[1].Value, Meaning: match.Groups[2].Value))
            .ToList();

        rows.Should().HaveCount(7, "every row states its conclusion in words");
        rows.Take(2).Should().OnlyContain(
            row => row.Label.Contains("(N:1)", StringComparison.Ordinal)
                || row.Label.Contains("(1:1)", StringComparison.Ordinal),
            "a cardinality row leads with the relationship name every ER editor shares, not a description of one");
        // The bar is a maximum. "Exactly one" is a fact of one constraint — a NOT NULL column under a
        // key — and the panel states it for that constraint; a key that judges every arrow may not.
        var legendStart = rendered.Document.IndexOf("<ul class=\"legend\">", StringComparison.Ordinal);
        var legendEnd = rendered.Document.IndexOf("</ul>", legendStart, StringComparison.Ordinal);
        var legend = rendered.Document[legendStart..legendEnd];

        legend.Should().NotContain("exactly one", "the key describes every arrow, so it cannot state a minimum");
        legend.Should().NotContain("one and only one", "that states a minimum a foreign key cannot enforce");

        // The shapes the key samples are defined once, in a block written ahead of everything that
        // references them — a key reaching back into the picture, or a second copy of a definition,
        // are both ways for the two to drift apart.
        var defined = rendered.Document.IndexOf("<svg class=\"marks\"", StringComparison.Ordinal);
        defined.Should().BeGreaterThan(-1);
        rendered.Document.IndexOf("<ul class=\"legend\">", StringComparison.Ordinal).Should().BeGreaterThan(defined);
        rendered.Document.IndexOf("<g id=\"viewport\"", StringComparison.Ordinal).Should().BeGreaterThan(defined);
    }

    [Fact]
    public void The_key_costs_the_picture_one_line_until_it_is_asked_for()
    {
        // The key teaches every mark once; a reader who has learnt them should not pay for the lesson
        // on every open, so it hangs under the header closed. It is a native disclosure — no script,
        // no state to lose — and every row is in the document either way, so opening it needs no
        // render and nothing the picture says is behind a control (D14.5).
        var document = Render(Shapes()).Document;

        Count(document, "<details class=\"keyed\">").Should().Be(1);
        Regex.Match(document, "<details class=\"keyed\"[^>]*>").Value.Should()
            .NotContain("open", "the picture keeps the screen until the key is asked for");

        var offered = document.IndexOf("<summary>", StringComparison.Ordinal);
        var rows = document.IndexOf("<ul class=\"legend\">", StringComparison.Ordinal);
        var closed = document.IndexOf("</details>", StringComparison.Ordinal);

        offered.Should().BeGreaterThan(-1, "a folded key that cannot be opened would lose the lesson");
        rows.Should().BeGreaterThan(offered, "the rows fold behind the line that offers them");
        closed.Should().BeGreaterThan(
            document.IndexOf("<b>collapsed</b>", StringComparison.Ordinal),
            "every row folds, not only the first few");
        Count(document, "<svg class=\"key\"").Should().Be(7, "folding the key removes no row from it");
    }

    [Fact]
    public void A_mark_never_prints_on_the_mark_of_the_end_beside_it()
    {
        // Keeping two arrows from merging is two rules: the border deals every end its own point at
        // least GraphEdgePorts.MinStep away from the next, and a mark's own ink has to fit inside that
        // room with a gap left over. A marker scales with the line it hangs on (markerUnits defaults to
        // stroke-width), so the line's width is part of its ink. All three numbers are read back from
        // the drawing rather than repeated here, so fattening a mark or thinning the room fails.
        var document = Render(Shapes()).Document;

        var ink = MarkInk(document, "head");
        var bar = MarkInk(document, "one");

        // Room enough for the ink is not room enough: a step that leaves a third of a pixel printed the
        // two heads as one thick mark, which is what made a card with two neighbours look like a card
        // with one arrow whose head was drawn twice (D4).
        (GraphEdgePorts.MinStep - ink).Should().BeGreaterThanOrEqualTo(6, "two neighbouring heads would print as one thick arrow");
        (GraphEdgePorts.MinStep - bar).Should().BeGreaterThanOrEqualTo(6, "a bar would print on the head of the end beside it");
    }

    [Fact]
    public void A_catalog_document_states_its_origin_in_the_words_it_always_had()
    {
        // Feature 11 changes what a document *can* describe, never what a catalog's document says: the
        // header keeps its folder, its format version and its moment (D8).
        var rendered = Render(CatalogGraphFixture.Create()
            .Table("public", "orders", 1200, Column("id", "bigint", isPrimaryKey: true))
            .Table("public", "customers", 40, Column("id", "bigint", isPrimaryKey: true))
            .Edge(
                CatalogGraphFixture.TableId("public", "orders"),
                CatalogGraphFixture.TableId("public", "customers"),
                "fk_orders_customers",
                ["customer_id"],
                ["id"])
            .WithoutData());

        // Assert
        rendered.Document.Should().Contain("<title>Relationships \u2014 fixture</title>");
        rendered.Document.Should().Contain("catalog <b title=\"C:\\catalogs\\fixture\">fixture</b>");
        rendered.Document.Should().Contain("format version 3");
        rendered.Document.Should().Contain("postgresql \u00b7 generated 2026-11-10 16:00");
        rendered.Document.Should().Contain("no row data was captured");
        rendered.Document.Should().Contain("aria-label=\"Search the catalog\"");
        rendered.Document.Should().Contain("no data captured");
        rendered.Document.Should().NotContain("live database");
        rendered.Document.Should().NotContain("rows are not counted here");
    }

    [Fact]
    public void A_live_document_names_the_database_and_never_speaks_of_a_catalog()
    {
        // A live read is not a catalog: there is no format to state and no row count was measured, so
        // every sentence that described a captured artefact is replaced by one that describes a read
        // (D8, D9). The outside table is drawn, so the register and the key state the live words too.
        var orders = CatalogGraphFixture.TableId("public", "orders");
        var absent = "table:absent.audit";
        var rendered = Render(CatalogGraphFixture.Create()
            .Live("testdb", "test.example.com:5432/testdb")
            .Table("public", "orders", columns: Column("id", "bigint", isPrimaryKey: true))
            .Table("public", "customers", columns: Column("id", "bigint", isPrimaryKey: true))
            .Outside(absent, "absent", "audit")
            .Edge(orders, CatalogGraphFixture.TableId("public", "customers"), "fk_orders_customers", ["customer_id"], ["id"])
            .Edge(orders, absent, "fk_orders_audit", ["audit_id"], ["id"]));

        // Assert — the origin the document states.
        rendered.Document.Should().Contain("<title>Relationships \u2014 testdb</title>");
        rendered.Document.Should().Contain("live database <b title=\"test.example.com:5432/testdb\">testdb</b>");
        rendered.Document.Should().Contain("postgresql \u00b7 read 2026-11-10 16:00");
        rendered.Document.Should().NotContain("format version");

        // Assert — the live words, and not one catalog literal anywhere in the document.
        rendered.Document.Should().Contain("rows are not counted here");
        rendered.Document.Should().Contain("not in this view");
        rendered.Document.Should().Contain("outside this view");
        rendered.Document.Should().Contain("the table it points at is not in this view");
        rendered.Document.Should().Contain("aria-label=\"Search this view\"");
        rendered.Document.Should().NotContain("no row data was captured");
        rendered.Document.Should().NotContain("no data captured");
        rendered.Document.Should().NotContain("not in this catalog");
        rendered.Document.Should().NotContain("outside the catalog");
        rendered.Document.Should().NotContain("the table it points at is not in this catalog");
        rendered.Document.Should().NotContain("Search the catalog");
        rendered.Document.Should().NotContain("a table the catalog does not name");
    }

    /// <summary>How wide one mark is drawn in the picture, in pixels, ink and all.</summary>
    private static double MarkInk(string document, string id)
    {
        var width = double.Parse(
            Regex.Match(document, "<marker id=\"" + id + "\"[^>]*markerWidth=\"([0-9.]+)\"").Groups[1].Value,
            CultureInfo.InvariantCulture);
        var stroke = double.Parse(
            Regex.Match(document, @"\.edge\{[^}]*stroke-width:([0-9.]+)").Groups[1].Value,
            CultureInfo.InvariantCulture);

        return width * stroke;
    }

    private static List<JsonElement> Edges(string island) =>
        [.. JsonDocument.Parse(island).RootElement.GetProperty("edges").EnumerateArray()];

    private static List<JsonElement> Nodes(string island) =>
        [.. JsonDocument.Parse(island).RootElement.GetProperty("nodes").EnumerateArray()];

    /// <summary>
    /// The three shapes side by side: a plain one-to-many, a child keyed on exactly its referencing
    /// column, and a link table keyed on the two columns it points through.
    /// </summary>
    private static CatalogGraphFixture Shapes()
    {
        var customers = CatalogGraphFixture.TableId("public", "customers");
        var issues = CatalogGraphFixture.TableId("public", "issues");
        var labels = CatalogGraphFixture.TableId("public", "labels");
        var id = CatalogGraphFixture.TableId("public", "issue_labels");

        return CatalogGraphFixture.Create()
            .Table("public", "customers", 40, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("customers_pkey", "id")
            .Table("public", "orders", 1200, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("orders_pkey", "id")
            .Table("public", "user_settings", 40, Column("user_id", "bigint"))
                .PrimaryKey("user_settings_pkey", "user_id")
            .Table("public", "issues", 90, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("issues_pkey", "id")
            .Table("public", "labels", 12, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("labels_pkey", "id")
            .Table("public", "issue_labels", 500, Column("issue_id", "bigint"), Column("label_id", "bigint"))
                .PrimaryKey("issue_labels_pkey", "issue_id", "label_id")
            .Edge(
                CatalogGraphFixture.TableId("public", "orders"),
                customers,
                "fk_orders_customers",
                ["customer_id"],
                ["id"],
                mandatory: true)
            .Edge(
                CatalogGraphFixture.TableId("public", "user_settings"),
                customers,
                "fk_settings_customers",
                ["user_id"],
                ["id"],
                mandatory: true)
            .Edge(id, issues, "fk_issue_labels_issue", ["issue_id"], ["id"], mandatory: true)
            .Edge(id, labels, "fk_issue_labels_label", ["label_id"], ["id"], mandatory: true);
    }

    private static string UnconnectedChip(CatalogGraphModel model) =>
        model.Chips.Single(chip => chip.Kind == EClusterChipKind.Unconnected).Index.ToString();

    private static double Number(Match match, string group) =>
        double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    /// <summary>The picture alone, from the viewport group on: the key above it samples the same marks.</summary>
    private static string Picture(string document) =>
        document[document.IndexOf("<g id=\"viewport\"", StringComparison.Ordinal)..];

    private static int Count(string text, string fragment)
    {
        var count = 0;
        var at = text.IndexOf(fragment, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(fragment, at + fragment.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static Rendered SmallCatalog(CatalogGraphFixture? fixture = null) =>
        Render(fixture ?? CatalogGraphFixture.Create()
            .Table("public", "orders", 1200, Column("id", "bigint", isPrimaryKey: true))
                .PrimaryKey("orders_pkey", "id")
                .Index(
                    "ix_orders_customer",
                    definition: "CREATE INDEX ix_orders_customer ON public.orders USING btree (customer_id)",
                    columns: ["customer_id"])
                .UniqueKey("uq_orders_external", "external_ref")
                .Check("orders_total_check", "total >= 0")
            .Table("public", "customers", 40, Column("id", "bigint", isPrimaryKey: true))
            .Edge(
                CatalogGraphFixture.TableId("public", "orders"),
                CatalogGraphFixture.TableId("public", "customers"),
                "fk_orders_customers",
                ["customer_id"],
                ["id"],
                mandatory: true,
                onDelete: "CASCADE"));

    /// <summary>A straight chain, one foreign key per table.</summary>
    private static CatalogGraphFixture Chain(int tables)
    {
        var fixture = CatalogGraphFixture.Create().Table("public", "t00", 10, Column("id", "integer", isPrimaryKey: true));
        for (var index = 1; index < tables; index++)
        {
            var name = $"t{index:D2}";
            fixture.Table("public", name, index * 7, Column("id", "integer", isPrimaryKey: true));
            fixture.Edge(
                CatalogGraphFixture.TableId("public", name),
                CatalogGraphFixture.TableId("public", $"t{index - 1:D2}"),
                $"fk_{name}",
                ["customer_id"],
                ["id"],
                mandatory: true,
                onDelete: "CASCADE");
        }

        return fixture;
    }

    /// <summary>A ring of <paramref name="members"/> tables, so the analyzer folds it behind one supernode.</summary>
    private static CatalogGraphFixture Ring(int members)
    {
        var fixture = CatalogGraphFixture.Create();
        for (var index = 0; index < members; index++)
        {
            fixture.Table("public", $"r{index:D2}", 5 + index, Column("id", "integer", isPrimaryKey: true));
        }

        for (var index = 0; index < members; index++)
        {
            fixture.Edge(
                CatalogGraphFixture.TableId("public", $"r{index:D2}"),
                CatalogGraphFixture.TableId("public", $"r{(index + 1) % members:D2}"),
                $"fk_r{index:D2}",
                ["id"],
                ["id"]);
        }

        return fixture;
    }

    private static RelationshipColumnView Column(string name, string type, bool isPrimaryKey = false) =>
        new(name, type, false, isPrimaryKey, false, false, null);

    private static Rendered Render(CatalogGraphFixture fixture)
    {
        var view = fixture.Build();
        var model = new CatalogGraphAnalyzer().Analyze(view);
        var layout = new GraphLayoutEngine().Compute(model);
        var document = new CatalogGraphHtmlRenderer().Render(view, model, layout, Moment);
        var island = document[
            (document.IndexOf("id=\"data\">", StringComparison.Ordinal) + "id=\"data\">".Length)..
            document.IndexOf("</script>", document.IndexOf("id=\"data\">", StringComparison.Ordinal), StringComparison.Ordinal)];

        return new Rendered(view, model, document, island);
    }

    private sealed record Rendered(
        RelationshipGraphView View,
        CatalogGraphModel Model,
        string Document,
        string Island);
}
