using System.Globalization;
using System.Text;
using FluentAssertions;
namespace UI.Tests.CatalogGraph;
/// <summary>Pins the reader of a rendered page with hand-written miniature documents and fake names.</summary>
[Trait("Speed", "Fast")]
public sealed class GraphRenderedDocumentTests
{
    /// <summary>A label printed at the width the document's own column names take.</summary>
    private const string Constraint = "test_parent_id → pk";
    [Fact]
    public void A_chord_behind_a_third_card_is_one_hidden_relation_and_one_occluding_card()
    {
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 200, 0)],
            [Edge(0, 0, 1, "M 100 20 L 400 20")]);
        var document = GraphRenderedDocument.Parse(page);
        document.HiddenRelations.Should().Be(1);
        document.DistinctOccludingCards.Should().Be(1);
        document.Occlusions.Should().Be(1);
    }
    [Fact]
    public void A_chord_that_touches_only_its_own_cards_is_not_hidden()
    {
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 200, 300)],
            [Edge(0, 0, 1, "M 100 20 L 400 20")]);
        GraphRenderedDocument.Parse(page).HiddenRelations.Should().Be(0);
    }
    [Fact]
    public void Two_chords_that_cross_are_one_crossing()
    {
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 400), Card(2, 400, 0), Card(3, 0, 400)],
            [Edge(0, 0, 1, "M 50 40 L 450 400"), Edge(1, 2, 3, "M 450 40 L 50 400")]);
        GraphRenderedDocument.Parse(page).Crossings.Should().Be(1);
    }
    [Fact]
    public void Two_marks_ten_pixels_apart_on_one_border_are_one_merged_mark()
    {
        var page = Page(
            [Card(0, 0, 0), Card(1, 0, 300), Card(2, 300, 300)],
            [Edge(0, 1, 0, "M 50 300 L 50 40"), Edge(1, 2, 0, "M 300 300 L 60 40")]);
        GraphRenderedDocument.Parse(page).MergedPortMarks.Should().Be(1);
    }
    [Fact]
    public void A_label_anchored_inside_a_card_is_counted_once()
    {
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 200, 0)],
            [Edge(0, 0, 1, "M 100 20 L 400 20")],
            [(0, 250, 20, "t")]);
        GraphRenderedDocument.Parse(page).LabelsOnCards.Should().Be(1);
    }
    [Fact]
    public void Two_labels_one_click_reveals_and_prints_on_one_place_are_one_overprint()
    {
        // Both constraints leave the same table, so selecting it speaks for both at once: their ink is one
        // garbled line and the reader cannot tell which of the two joins which column.
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 400, 300)],
            [Edge(0, 0, 1, "M 100 20 L 400 20"), Edge(1, 0, 2, "M 100 30 L 400 320")],
            [(0, 200, 20, Constraint), (1, 210, 24, Constraint)]);
        GraphRenderedDocument.Parse(page).LabelOverprints.Should().Be(1);
    }
    [Fact]
    public void Two_labels_carried_one_port_step_apart_are_not_an_overprint()
    {
        // The lanes of a parallel family are dealt one port step apart, which is the room a stacked pair of
        // labels has and the room the picture promises them.
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 400, 300)],
            [Edge(0, 0, 1, "M 100 20 L 400 20"), Edge(1, 0, 2, "M 100 38 L 400 338")],
            [(0, 200, 20, Constraint), (1, 200, 38, Constraint)]);
        GraphRenderedDocument.Parse(page).LabelOverprints.Should().Be(0);
    }
    [Fact]
    public void Ink_that_meets_but_no_single_click_reveals_is_not_an_overprint()
    {
        // Two relations of two unrelated pairs cross and their words meet at the crossing; no selection ever
        // shows both of them, so a reader is never handed one line where he expects two.
        var page = Page(
            [Card(0, 0, 0), Card(1, 800, 300), Card(2, 0, 300), Card(3, 800, 0)],
            [Edge(0, 0, 1, "M 100 20 L 850 320"), Edge(1, 2, 3, "M 100 320 L 850 20")],
            [(0, 470, 170, Constraint), (1, 480, 172, Constraint)]);
        GraphRenderedDocument.Parse(page).LabelOverprints.Should().Be(0);
    }
    [Fact]
    public void An_away_card_and_an_away_stroke_are_not_painted()
    {
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 200, 0, away: true)],
            [Edge(0, 0, 1, "M 100 20 L 400 20"), Edge(1, 0, 1, "M 100 30 L 400 30", away: true)]);
        var document = GraphRenderedDocument.Parse(page);
        document.PaintedCards.Should().Be(2);
        document.PaintedRelations.Should().Be(1);
        document.HiddenRelations.Should().Be(0);
    }
    [Fact]
    public void A_cubic_whose_control_points_stay_outside_a_card_while_its_arc_enters_it_is_hidden()
    {
        // Controls at y = -60 and y = 80 straddle the card (y 0..40); the sampled arc passes through it.
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 195, 0, width: 10)],
            [Edge(0, 0, 1, "M 100 20 C 200 -60 300 100 400 20")]);
        GraphRenderedDocument.Parse(page).HiddenRelations.Should().Be(1);
    }
    [Fact]
    public void A_card_the_reader_carried_out_of_the_way_is_measured_where_it_stands()
    {
        // A saved page keeps the rect the layout wrote and carries the drag as a translate on the group.
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 200, 0, shift: "translate(0,300)")],
            [Edge(0, 0, 1, "M 100 20 L 400 20")]);
        GraphRenderedDocument.Parse(page).HiddenRelations.Should().Be(0);
    }
    [Fact]
    public void A_card_the_reader_carried_over_a_chord_hides_that_relation()
    {
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 200, 300, shift: "translate(0, -300)")],
            [Edge(0, 0, 1, "M 100 20 L 400 20")]);
        GraphRenderedDocument.Parse(page).HiddenRelations.Should().Be(1);
    }
    [Fact]
    public void A_loop_that_followed_a_carried_card_is_measured_where_it_stands()
    {
        // The loop keeps its written path; the browser moves it with a translate on the path itself.
        var page = Page(
            [Card(0, 0, 0), Card(1, 400, 0), Card(2, 900, 860)],
            [Edge(0, 0, 0, "M 900 900 C 900 860 940 860 940 900", shift: "translate(-860,-860)")]);
        GraphRenderedDocument.Parse(page).HiddenLoops.Should().Be(0);
    }
    private static string Card(int index, double x, double y, bool away = false, double width = 100, string shift = "")
    {
        var carried = shift.Length == 0 ? string.Empty : $" transform=\"{shift}\"";
        return string.Create(CultureInfo.InvariantCulture,
            $"""<g class="node cap{(away ? " away" : string.Empty)}" data-n="{index}"{carried}><title>t</title><rect x="{x}" y="{y}" width="{width}" height="40" rx="7"/></g>""");
    }
    private static (int Index, int Child, int Parent, string D, bool Away, string Shift) Edge(
        int index, int child, int parent, string d, bool away = false, string shift = "") =>
        (index, child, parent, d, away, shift);
    private static string Page(
        string[] cards,
        (int Index, int Child, int Parent, string D, bool Away, string Shift)[] edges,
        (int Edge, double X, double Y, string Text)[]? labels = null)
    {
        var html = new StringBuilder("<g class=\"edges\">");
        foreach (var (index, _, _, d, away, shift) in edges)
        {
            var cls = away ? "edge away" : "edge";
            var carried = shift.Length == 0 ? string.Empty : $" transform=\"{shift}\"";
            html.Append(CultureInfo.InvariantCulture, $"<path class=\"{cls}\" data-e=\"{index}\" d=\"{d}\"{carried}/>");
            html.Append(CultureInfo.InvariantCulture, $"<path class=\"{cls} pick\" data-e=\"{index}\" d=\"{d}\"{carried}/>");
        }
        foreach (var (edge, x, y, text) in labels ?? [])
        {
            html.Append(CultureInfo.InvariantCulture, $"<text class=\"elabel quiet\" data-e=\"{edge}\" x=\"{x}\" y=\"{y}\">{text}</text>");
        }
        html.Append("</g><g class=\"nodes\">").AppendJoin(string.Empty, cards).Append("</g>");
        var island = string.Join(",", edges.Select(edge => string.Create(CultureInfo.InvariantCulture,
            $"{{\"i\":{edge.Index},\"c\":{edge.Child},\"p\":{edge.Parent},\"self\":{(edge.Child == edge.Parent ? "true" : "false")}}}")));
        html.Append(CultureInfo.InvariantCulture, $"<script type=\"application/json\" id=\"data\">{{\"edges\":[{island}]}}</script>");
        return html.ToString();
    }
}
