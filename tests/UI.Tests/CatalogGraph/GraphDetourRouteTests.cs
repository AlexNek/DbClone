using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Locks the routing phase of last resort: a relation with no clear chord and no bow that clears the cards
/// in its way is taken round them, which is what every diagram editor does with such a relation.
/// </summary>
public sealed class GraphDetourRouteTests
{
    private static readonly NodePlacement Child = new(0, new GraphPoint(0, 0), 120, 40);
    private static readonly NodePlacement Parent = new(1, new GraphPoint(600, 0), 120, 40);

    [Fact]
    public void A_relation_whose_chord_is_blocked_is_taken_round_the_cards_in_its_way()
    {
        // One card stands in the chord between the two tables and is too tall to be cleared by a bow that
        // leaves both ports along their normals: exactly the relation the router used to call undrawable.
        var wall = new NodePlacement(2, new GraphPoint(280, -200), 120, 440);
        NodePlacement[] cards = [Child, Parent, wall];

        var points = GraphDetourRoute.Build(
            new GraphEdgePort(EEdgeSide.Right, new GraphPoint(120, 20)),
            new GraphEdgePort(EEdgeSide.Left, new GraphPoint(600, 20)),
            cards,
            (0, 1));

        points.Should().NotBeNull("a relation is drawn, never reported as one the picture cannot hold");
        points!.Length.Should().Be(1 + (3 * ((points.Length - 1) / 3)), "the route is a cubic sequence");
        points[0].Should().Be(new GraphPoint(120, 20), "it starts on the child's port");
        points[^1].Should().Be(new GraphPoint(600, 20), "and ends on the parent's port");

        var flat = GraphGeometry.Flatten(points);
        flat.IsComplete.Should().BeTrue("the curve can be trusted as the route it stands for");

        foreach (var card in cards.Where(card => card.NodeIndex == wall.NodeIndex))
        {
            for (var at = 1; at < flat.Points.Count; at++)
            {
                var hit = GraphGeometry.SegmentHitsRect(
                    flat.Points[at - 1],
                    flat.Points[at],
                    card,
                    GraphGeometryContract.CardCenterlineClearance);

                hit.Should().BeFalse($"no stroke passes through the card that stands in the way (point {at})");
            }
        }
    }

    [Fact]
    public void The_way_round_is_the_same_every_time()
    {
        NodePlacement[] cards = [Child, Parent, new NodePlacement(2, new GraphPoint(300, -160), 120, 360)];
        var start = new GraphEdgePort(EEdgeSide.Right, new GraphPoint(120, 20));
        var end = new GraphEdgePort(EEdgeSide.Left, new GraphPoint(600, 20));

        var first = GraphDetourRoute.Build(start, end, cards, (0, 1));
        var second = GraphDetourRoute.Build(start, end, cards, (0, 1));

        first.Should().NotBeNull();
        second.Should().Equal(first, "the same picture is drawn on every machine");
    }

    [Fact]
    public void An_open_field_needs_no_detour_and_is_left_to_the_straight_chord()
    {
        var points = GraphDetourRoute.Build(
            new GraphEdgePort(EEdgeSide.Right, new GraphPoint(120, 20)),
            new GraphEdgePort(EEdgeSide.Left, new GraphPoint(600, 20)),
            [Child, Parent],
            (0, 1));

        points.Should().NotBeNull("the way exists whether or not it is needed");
        var flat = GraphGeometry.Flatten(points!);
        flat.Points.Should().OnlyContain(point => point.Y == 20, "nothing stands in the way, so nothing is turned");
    }
}
