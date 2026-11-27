using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Locks where a relation states its columns: on its own route, at the share of it the printed text can be
/// read at, and never on top of the label of another relation the same click reveals.
/// </summary>
[Trait("Speed", "Fast")]
public sealed class GraphLabelPlacementTests
{
    private const string Source = "source_requirement_id → id";
    private const string Target = "target_requirement_id → id";
    private const string Note = "reviewed_by → id";

    [Fact]
    public void Two_labels_on_lanes_a_port_step_apart_are_printed_one_above_the_other()
    {
        // The reported pair: two constraints join the same two tables, so their lanes run one port step apart
        // and each was handed the share of its own line the family rule gives it — a third of one and two thirds
        // of the other, which on a tilted 200 px corridor is 7 px apart while the text needs 15. The lanes are
        // what the picture has to offer, and the labels have to fit inside them instead of on each other.
        var lanes = new[] { Lane(0, 200, 100, 90, 1 / 3.0), Lane(1, 200, 118, 108, 2 / 3.0) };

        var placed = GraphLabelPlacement.Place(
            [Card(0, 0, 90), Card(1, 320, 80)],
            lanes,
            [(0, 1), (0, 1)],
            [Source, Target]);

        var first = Ink(placed[0], Source);
        var second = Ink(placed[1], Target);
        first.Meets(second).Should().BeFalse("two constraints revealed by one click read as one garbled relation");
        placed[0].LabelPoint.X.Should().BeApproximately(placed[1].LabelPoint.X, 1e-6, "the lanes are one below the other, so their words are too");
    }

    [Fact]
    public void Three_labels_in_one_corridor_are_staggered_along_it_so_every_one_of_them_reads()
    {
        // These lanes are 2 px apart, so no share of them separates their text: the only room left is along the
        // route, and a corridor this long holds three statements of these columns when each takes its own third
        // of it. A label is never dropped and never moved off its own line to make that happen.
        var lanes = new[] { Lane(0, 500, 390, 390, 0.25), Lane(1, 500, 392, 392, 0.5), Lane(2, 500, 394, 394, 0.75) };

        var placed = GraphLabelPlacement.Place(
            [Card(0, 0, 380), Card(1, 620, 380)],
            lanes,
            [(0, 1), (0, 1), (0, 1)],
            [Source, Target, Note]);

        for (var first = 0; first < placed.Count; first++)
        {
            for (var second = first + 1; second < placed.Count; second++)
            {
                Ink(placed[first], new[] { Source, Target, Note }[first])
                    .Meets(Ink(placed[second], new[] { Source, Target, Note }[second]))
                    .Should().BeFalse($"constraints {first} and {second} are revealed by the same click");
            }
        }

        placed[0].LabelPoint.X.Should().BeLessThan(placed[2].LabelPoint.X, "the stagger keeps the order the lanes are drawn in");
    }

    [Fact]
    public void A_label_that_already_reads_is_left_on_the_place_its_route_was_handed_with()
    {
        var lane = Lane(0, 500, 400, 400, 0.5);

        var placed = GraphLabelPlacement.Place([Card(0, 0, 384), Card(1, 620, 384)], [lane], [(0, 1)], [Source]);

        placed[0].LabelPoint.X.Should().BeApproximately(lane.LabelPoint.X, 1e-6, "a label nobody is printed over has nothing to answer");
        placed[0].LabelPoint.Y.Should().BeApproximately(lane.LabelPoint.Y, 1e-6, "a label nobody is printed over has nothing to answer");
    }

    [Fact]
    public void Every_label_stays_on_its_own_route_and_no_route_is_moved()
    {
        var lanes = new[]
        {
            Lane(0, 200, 100, 90, 1 / 3.0),
            Lane(1, 200, 118, 108, 2 / 3.0),
            Lane(2, 200, 136, 126, 0.5),
            Lane(3, 260, 420, 300, 0.5),
        };
        var labels = new[] { Source, Target, Note, Source };

        var placed = GraphLabelPlacement.Place(
            [Card(0, 0, 90), Card(1, 320, 80), Card(2, 40, 400)],
            lanes,
            [(0, 1), (0, 1), (0, 1), (2, 1)],
            labels);

        for (var edge = 0; edge < lanes.Length; edge++)
        {
            var (from, to) = (lanes[edge].Points[0], lanes[edge].Points[^1]);
            placed[edge].Points.Should().Equal(lanes[edge].Points, "a label never moves the line it states");
            placed[edge].Shape.Should().Be(lanes[edge].Shape);
            placed[edge].Geometry.Should().Be(lanes[edge].Geometry);
            placed[edge].LabelPoint.X.Should().BeInRange(Math.Min(from.X, to.X), Math.Max(from.X, to.X), "the words stay on their own arrow");
            placed[edge].LabelPoint.Y.Should().BeInRange(Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y), "the words stay on their own arrow");
        }
    }

    /// <summary>A lane between two ports, carrying the label place the family rule handed it with.</summary>
    private static EdgeRoute Lane(int edge, double length, double child, double parent, double share) =>
        new(
            edge,
            EEdgeShape.Line,
            EEdgeGeometry.Straight,
            [new GraphPoint(120, child), new GraphPoint(120 + length, parent)],
            PointAt(length, child, parent, share));

    private static GraphPoint PointAt(double length, double child, double parent, double share) =>
        new(120 + (share * length), child + (share * (parent - child)));

    private static NodePlacement Card(int index, double x, double y) => new(index, new GraphPoint(x, y), 120, 40);

    private static GraphLabelInk Ink(EdgeRoute route, string text) =>
        GraphLabelInk.Around(route.LabelPoint, text, 0, 1);
}
