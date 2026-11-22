using System.Globalization;
using System.Text;
using FluentAssertions;
namespace UI.Tests.CatalogGraph;
/// <summary>Pins the reader of a rendered page with hand-written miniature documents and fake names.</summary>
[Trait("Speed", "Fast")]
public sealed class GraphRenderedDocumentTests
{
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
            labels: [(0, 250, 20)]);
        GraphRenderedDocument.Parse(page).LabelsOnCards.Should().Be(1);
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
    private static string Card(int index, double x, double y, bool away = false, double width = 100) =>
        string.Create(CultureInfo.InvariantCulture,
            $"""<g class="node cap{(away ? " away" : string.Empty)}" data-n="{index}"><title>t</title><rect x="{x}" y="{y}" width="{width}" height="40" rx="7"/></g>""");
    private static (int Index, int Child, int Parent, string D, bool Away) Edge(int index, int child, int parent, string d, bool away = false) =>
        (index, child, parent, d, away);
    private static string Page(
        string[] cards,
        (int Index, int Child, int Parent, string D, bool Away)[] edges,
        (int Edge, double X, double Y)[]? labels = null)
    {
        var html = new StringBuilder("<g class=\"edges\">");
        foreach (var (index, _, _, d, away) in edges)
        {
            var cls = away ? "edge away" : "edge";
            html.Append(CultureInfo.InvariantCulture, $"<path class=\"{cls}\" data-e=\"{index}\" d=\"{d}\"/>");
            html.Append(CultureInfo.InvariantCulture, $"<path class=\"{cls} pick\" data-e=\"{index}\" d=\"{d}\"/>");
        }
        foreach (var (edge, x, y) in labels ?? [])
        {
            html.Append(CultureInfo.InvariantCulture, $"<text class=\"elabel quiet\" data-e=\"{edge}\" x=\"{x}\" y=\"{y}\">t</text>");
        }
        html.Append("</g><g class=\"nodes\">").AppendJoin(string.Empty, cards).Append("</g>");
        var island = string.Join(",", edges.Select(edge => string.Create(CultureInfo.InvariantCulture,
            $"{{\"i\":{edge.Index},\"c\":{edge.Child},\"p\":{edge.Parent},\"self\":{(edge.Child == edge.Parent ? "true" : "false")}}}")));
        html.Append(CultureInfo.InvariantCulture, $"<script type=\"application/json\" id=\"data\">{{\"edges\":[{island}]}}</script>");
        return html.ToString();
    }
}
