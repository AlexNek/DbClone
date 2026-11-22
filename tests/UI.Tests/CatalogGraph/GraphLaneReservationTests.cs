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
    public void A_hub_spoke_covered_by_a_card_of_the_inner_ring_is_cleared_and_every_card_keeps_its_radius()
    {
        // The hub's spoke from the outer card runs straight through the inner card before the pass.
        double[] x = [0, 300, 600];
        double[] y = [0, 0, 0];
        double[] widths = [100, 100, 100];
        (int Child, int Parent)[] relations = [(1, 0), (2, 0)];
        Covered(x, y, widths, relations).Should().Be(1, "the fixture starts with one covered spoke");
        GraphLaneReservation.Reserve(
            [0, 1, 2],
            x,
            y,
            widths,
            [0, 1, 2],
            new Dictionary<int, (double Radius, double Step)> { [0] = (0, 0), [1] = (300, 160), [2] = (600, 160) },
            relations);
        Covered(x, y, widths, relations).Should().Be(0, "every spoke of the hub runs through open ground");
        Math.Sqrt((x[1] * x[1]) + (y[1] * y[1])).Should().BeApproximately(300, 1e-6);
        Math.Sqrt((x[2] * x[2]) + (y[2] * y[2])).Should().BeApproximately(600, 1e-6);
    }

    [Fact]
    public void A_ring_too_tight_to_turn_grows_outward_until_its_chord_clears_the_core()
    {
        // Two ring cards at ±60° join each other; the chord runs at x = 150 through the wide core. Each end
        // has a neighbour standing exactly at the gap's edge on both sides, so one angular step either way
        // would overlap it: only a growth of the ring can clear the chord.
        var (ex, ey) = (300 * Math.Cos(Math.PI / 3), 300 * Math.Sin(Math.PI / 3));
        var spacing = 100 + GraphPlacementConstants.CardGap + 0.4;
        double[] x = [0, ex, ex, ex + spacing, ex - spacing, ex + spacing, ex - spacing];
        double[] y = [0, -ey, ey, -ey, -ey, ey, ey];
        double[] widths = [300, 100, 100, 100, 100, 100, 100];
        (int Child, int Parent)[] relations = [(1, 2)];
        Covered(x, y, widths, relations).Should().Be(1, "the fixture starts with the chord behind the core");
        GraphLaneReservation.Reserve(
            [0, 1, 2, 3, 4, 5, 6],
            x,
            y,
            widths,
            [0, 1, 1, 1, 1, 1, 1],
            new Dictionary<int, (double Radius, double Step)> { [0] = (0, 0), [1] = (300, 160) },
            relations);
        Covered(x, y, widths, relations).Should().Be(0, "the grown ring carries the chord clear of the core");
        Math.Sqrt((x[1] * x[1]) + (y[1] * y[1])).Should().BeApproximately(460, 1e-6, "the ring grew by its recorded step");
        Math.Atan2(y[1], x[1]).Should().BeApproximately(-Math.PI / 3, 1e-9, "a growth keeps the angles");
        Overlaps(x, y, widths).Should().Be(0);
    }

    [Fact]
    public void The_radial_hub_picture_has_no_covered_spoke_and_no_merged_port_mark()
    {
        // This pins the radial seed through repair and the renderer on the hub fixture; the fan is already
        // clear without the reservation, which the two direct tests above exercise instead.
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

    private static int Covered(double[] x, double[] y, double[] widths, (int Child, int Parent)[] relations) =>
        relations.Count(relation => Enumerable.Range(0, x.Length).Any(at =>
            at != relation.Child && at != relation.Parent
            && GraphGeometry.SegmentHitsCardEnvelope(
                Card(relation.Child, x, y, widths).Centre,
                Card(relation.Parent, x, y, widths).Centre,
                Card(at, x, y, widths))));

    private static int Overlaps(double[] x, double[] y, double[] widths)
    {
        var count = 0;
        for (var a = 0; a < x.Length; a++)
        {
            for (var b = a + 1; b < x.Length; b++)
            {
                if (Math.Abs(x[a] - x[b]) < ((widths[a] + widths[b]) / 2) && Math.Abs(y[a] - y[b]) < GraphCard.Height)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static NodePlacement Card(int at, double[] x, double[] y, double[] widths) =>
        new(at, new GraphPoint(x[at] - (widths[at] / 2), y[at] - (GraphCard.Height / 2)), widths[at], GraphCard.Height);
}
