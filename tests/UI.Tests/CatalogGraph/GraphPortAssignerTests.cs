using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks continuous port assignment (refactor 14, milestone D).</summary>
public sealed class GraphPortAssignerTests
{
    private const double Height = 40;

    [Fact]
    public void Ports_on_one_side_are_a_port_step_apart_and_sit_on_the_border_inside_the_corner_inset()
    {
        var nodes = new[] { Card(0, 0, 0, 200), Card(1, 400, -30), Card(2, 400, 20), Card(3, -300, 0), Card(4, 60, 300), Card(5, 60, -300) };
        var edges = new (int, int)[] { (1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (0, 1) };

        var assignment = GraphPortAssigner.Assign(nodes, edges);

        assignment.IsComplete.Should().BeTrue();
        var ports = assignment.Ends.SelectMany((ends, edge) => new[] { (Node: edges[edge].Item1, Port: ends!.Child), (Node: edges[edge].Item2, Port: ends.Parent) }).ToArray();
        foreach (var port in ports)
        {
            var card = nodes[port.Node];
            var onBorder = port.Port.Side switch
            {
                EEdgeSide.Right => port.Port.Point.X == card.Right,
                EEdgeSide.Left => port.Port.Point.X == card.TopLeft.X,
                EEdgeSide.Top => port.Port.Point.Y == card.TopLeft.Y,
                _ => port.Port.Point.Y == card.Bottom,
            };
            onBorder.Should().BeTrue();
        }

        foreach (var side in ports.GroupBy(port => (port.Node, port.Port.Side)))
        {
            var along = side.Select(port => side.Key.Side is EEdgeSide.Top or EEdgeSide.Bottom ? port.Port.Point.X - nodes[side.Key.Node].TopLeft.X : port.Port.Point.Y - nodes[side.Key.Node].TopLeft.Y)
                .OrderBy(value => value).ToArray();
            along.Should().OnlyContain(value => value >= GraphGeometryContract.CardCornerClearance - 1e-9);
            for (var at = 1; at < along.Length; at++)
            {
                (along[at] - along[at - 1]).Should().BeGreaterThanOrEqualTo(GraphGeometryContract.MinimumPortStep - 1e-9);
            }
        }
    }
    [Fact]
    public void Ends_on_a_side_are_ordered_by_where_the_other_end_stands_so_a_fan_does_not_braid()
    {
        var nodes = new[] { Card(0, 0, 0, 200), Card(1, 400, 300), Card(2, 400, -300) };

        var assignment = GraphPortAssigner.Assign(nodes, [(1, 0), (2, 0)]);

        assignment.Ends[1]!.Parent.Point.Y.Should().BeLessThan(assignment.Ends[0]!.Parent.Point.Y);
    }

    [Fact]
    public void Two_parallel_relations_keep_one_order_at_both_ends_so_they_do_not_braid()
    {
        var nodes = new[] { Card(0, 0, 0, 200), Card(1, 500, 0, 200) };

        var assignment = GraphPortAssigner.Assign(nodes, [(0, 1), (0, 1)]);

        assignment.Ends.Select(ends => ends!.Child.Point.Y).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        assignment.Ends.Select(ends => ends!.Parent.Point.Y).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Three_parallel_relations_get_distinct_ports_at_both_ends()
    {
        var nodes = new[] { Card(0, 0, 0, 200), Card(1, 500, 0, 200) };

        var assignment = GraphPortAssigner.Assign(nodes, [(0, 1), (0, 1), (0, 1)]);

        assignment.IsComplete.Should().BeTrue();
        assignment.Ends.Select(ends => ends!.Child.Point).Distinct().Should().HaveCount(3);
        assignment.Ends.Select(ends => ends!.Parent.Point).Distinct().Should().HaveCount(3);
    }
    [Fact]
    public void A_self_reference_gets_two_distinct_ports_a_step_apart()
    {
        var nodes = new[] { Card(0, 0, 0, 200) };

        var ends = GraphPortAssigner.Assign(nodes, [(0, 0)]).Ends.Single()!;

        GraphGeometry.Distance(ends.Child.Point, ends.Parent.Point).Should().BeGreaterThanOrEqualTo(GraphGeometryContract.MinimumPortStep - 1e-9);
    }

    [Fact]
    public void A_card_whose_border_cannot_hold_its_ends_reports_port_capacity_and_never_a_coincident_port()
    {
        var edges = Enumerable.Range(1, 14).Select(node => (node, 0)).ToArray();
        var nodes = new[] { Card(0, 0, 0, 72) }
            .Concat(Enumerable.Range(1, 14).Select(node => Card(node, (node % 7 * 150) - 500, node < 8 ? -300 : 300, 72)))
            .ToArray();

        var narrow = GraphPortAssigner.Assign(nodes, edges);

        narrow.IsComplete.Should().BeFalse();
        narrow.Failures.Should().OnlyContain(failure => failure.Kind == ERouteFailureKind.PortCapacity && failure.BlockingCards.Single() == 0);
        var ports = narrow.Ends.Where(ends => ends is not null).Select(ends => ends!.Parent.Point).ToArray();
        ports.Distinct().Should().HaveCount(ports.Length, "no coincident endpoint is ever placed");
    }

    [Fact]
    public void Widening_the_card_to_the_minimum_width_resolves_the_capacity_failure()
    {
        var edges = Enumerable.Range(1, 14).Select(node => (node, 0)).ToArray();
        var width = Math.Max(72, GraphPortAssigner.MinimumWidth(14, Height));
        var nodes = new[] { Card(0, 0, 0, width) }
            .Concat(Enumerable.Range(1, 14).Select(node => Card(node, (node % 7 * 150) - 500, node < 8 ? -300 : 300, 72)))
            .ToArray();

        var wide = GraphPortAssigner.Assign(nodes, edges);

        wide.IsComplete.Should().BeTrue();
        wide.Ends.Select(ends => ends!.Parent.Point).Distinct().Should().HaveCount(14);
    }

    [Fact]
    public void Capacity_follows_the_corner_inset_and_the_port_step()
    {
        GraphPortAssigner.Capacity(10).Should().Be(0);
        GraphPortAssigner.Capacity(GraphGeometryContract.CardCornerClearance * 2 + GraphGeometryContract.StrokeWidth).Should().Be(1);
        GraphPortAssigner.Capacity((GraphGeometryContract.CardCornerClearance * 2) + GraphGeometryContract.StrokeWidth + GraphGeometryContract.MinimumPortStep).Should().Be(2);
    }

    [Fact]
    public void The_same_input_gives_the_same_ports_twice()
    {
        var nodes = new[] { Card(0, 0, 0, 200), Card(1, 400, 300, 100), Card(2, 400, -300, 100) };
        var edges = new (int, int)[] { (1, 0), (2, 0), (1, 2) };

        GraphPortAssigner.Assign(nodes, edges).Ends.Should().BeEquivalentTo(GraphPortAssigner.Assign(nodes, edges).Ends);
    }

    private static NodePlacement Card(int index, double x, double y, double width = 100) =>
        new(index, new GraphPoint(x, y), width, Height);
}
