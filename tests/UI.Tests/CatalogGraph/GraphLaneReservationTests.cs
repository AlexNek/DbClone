using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;
namespace UI.Tests.CatalogGraph;
/// <summary>Locks the radial lane reservation: it clears a covered relation by turning cards along their ring.</summary>
[Trait("Speed", "Fast")]
public sealed class GraphLaneReservationTests
{
    [Fact]
    public void A_card_standing_between_two_others_is_cleared_by_turning_them_along_their_ring()
    {
        double[] x = [0, 400, -400];
        double[] y = [0, 0, 0];
        double[] widths = [100, 100, 100];
        GraphLaneReservation.Reserve(
            [0, 1, 2],
            x,
            y,
            widths,
            [0, 1, 1],
            new Dictionary<int, (double Radius, double Step)> { [0] = (0, 0), [1] = (400, 160) },
            [(1, 2)]);
        var core = Card(0, x, y, widths);
        GraphGeometry.SegmentHitsCardEnvelope(Card(1, x, y, widths).Centre, Card(2, x, y, widths).Centre, core)
            .Should().BeFalse("the stroke between the two ring cards no longer runs behind the core");
        Math.Sqrt((x[1] * x[1]) + (y[1] * y[1])).Should().BeApproximately(400, 1e-6, "a card turns along its ring and keeps its radius");
        Math.Sqrt((x[2] * x[2]) + (y[2] * y[2])).Should().BeApproximately(400, 1e-6);
    }

    [Fact]
    public void A_picture_with_no_covered_relation_is_left_exactly_as_it_was()
    {
        double[] x = [0, 400, -400];
        double[] y = [0, 300, 300];
        double[] widths = [100, 100, 100];
        GraphLaneReservation.Reserve(
            [0, 1, 2],
            x,
            y,
            widths,
            [0, 1, 1],
            new Dictionary<int, (double Radius, double Step)> { [0] = (0, 0), [1] = (500, 160) },
            [(1, 2)]);
        x.Should().Equal(0, 400, -400);
        y.Should().Equal(0, 300, 300);
    }

    [Fact]
    public void The_busiest_table_of_a_hub_has_no_covered_relation_and_no_merged_port_mark()
    {
        var profile = GraphQualityFixtures.Profile("hub");
        var seed = GraphRadialSeed.Place(profile);
        var outcome = new GraphLayoutRepair(0).Repair(seed, profile, new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations), default, hunt: false);
        outcome.IsSuccess.Should().BeTrue();
        var candidate = outcome.Candidate!;
        GraphReaderMeasures.Of(candidate, profile).BusiestFanHidden.Should().Be(0);
        var view = GraphQualityFixtures.View("hub");
        var html = new CatalogGraphHtmlRenderer().Render(
            view,
            new CatalogGraphAnalyzer().Analyze(view),
            new CatalogGraphLayout(candidate.Layout.Nodes, candidate.Routes, candidate.Layout.Width, candidate.Layout.Height),
            DateTimeOffset.UnixEpoch);
        GraphRenderedDocument.Parse(html).MergedPortMarks.Should().Be(0);
    }

    private static NodePlacement Card(int at, double[] x, double[] y, double[] widths) =>
        new(at, new GraphPoint(x[at] - (widths[at] / 2), y[at] - (GraphCard.Height / 2)), widths[at], GraphCard.Height);
}
