using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

[Trait("Speed", "Fast")]
public sealed class GraphClusterPlacementOptimizerTests
{
    [Fact]
    public void Remote_related_groups_move_closer_in_both_axes_without_losing_their_gap()
    {
        var profile = GraphLayoutProfile.Create([100d, 100d], [(1, 0)]);
        var groups = new GraphClusterSet([0, 1], new IReadOnlyList<int>[] { new[] { 0 }, new[] { 1 } }, 0.5);
        GraphPoint[] start = [new(0, 0), new(3000, 2000)];

        var result = GraphClusterPlacementOptimizer.Improve(profile, groups, start);

        Length(profile, result).Should().BeLessThan(Length(profile, start) / 2);
        Math.Abs(result[1].X - result[0].X).Should().BeLessThan(3000);
        Math.Abs(result[1].Y - result[0].Y).Should().BeLessThan(2000);
        Separated(profile, groups, result).Should().BeTrue();
        GraphLayoutScore.MeasureRankViolations(profile, Cards(profile, result)).Should().Be(0);
        start.Should().Equal(new GraphPoint(0, 0), new GraphPoint(3000, 2000));
    }

    [Fact]
    public void Cards_can_change_places_inside_a_group_to_remove_crossed_relations()
    {
        var profile = GraphLayoutProfile.Create([100d, 100d, 100d, 100d], [(2, 0), (3, 1)]);
        var groups = new GraphClusterSet([0, 0, 1, 1], new IReadOnlyList<int>[] { new[] { 0, 1 }, new[] { 2, 3 } }, 0.5);
        GraphPoint[] start = [new(0, 0), new(200, 0), new(200, 1000), new(0, 1000)];

        var result = GraphClusterPlacementOptimizer.Improve(profile, groups, start);
        var cards = Cards(profile, result);

        GraphGeometry.SegmentsCross(cards[2].Centre, cards[0].Centre, cards[3].Centre, cards[1].Centre).Should().BeFalse();
        GraphLayoutScore.MeasureBlockedRelations(profile, cards).Should().Be(0);
        GraphLayoutScore.MeasureRankViolations(profile, cards).Should().Be(0);
        Separated(profile, groups, result).Should().BeTrue();
        Length(profile, result).Should().BeLessThan(Length(profile, start));
    }

    [Fact]
    public void A_card_standing_on_an_unrelated_relation_moves_out_of_its_way()
    {
        var profile = GraphLayoutProfile.Create([100d, 100d, 100d], [(2, 0), (1, 0)]);
        var groups = new GraphClusterSet([0, 0, 0], new IReadOnlyList<int>[] { new[] { 0, 1, 2 } }, 0.5);
        GraphPoint[] start = [new(0, 0), new(0, 200), new(0, 400)];
        GraphLayoutScore.MeasureBlockedRelations(profile, Cards(profile, start)).Should().Be(1);

        var result = GraphClusterPlacementOptimizer.Improve(profile, groups, start);

        GraphLayoutScore.MeasureBlockedRelations(profile, Cards(profile, result)).Should().Be(0);
        GraphLayoutScore.MeasureRankViolations(profile, Cards(profile, result)).Should().Be(0);
        Length(profile, result).Should().BeLessThanOrEqualTo(Length(profile, start));
    }

    [Fact]
    public void The_same_placement_has_the_same_result_on_repeated_runs()
    {
        var profile = GraphLayoutProfile.Create([100d, 180d, 120d], [(1, 0), (2, 0)]);
        var groups = new GraphClusterSet([0, 1, 1], new IReadOnlyList<int>[] { new[] { 0 }, new[] { 1, 2 } }, 0.5);
        GraphPoint[] start = [new(0, 0), new(1200, 900), new(1500, 900)];

        var first = GraphClusterPlacementOptimizer.Improve(profile, groups, start);
        GraphClusterPlacementOptimizer.Improve(profile, groups, start).Should().Equal(first);
    }

    [Fact]
    public void Cancellation_is_honored_before_any_placement_is_changed()
    {
        var profile = GraphLayoutProfile.Create([100d], []);
        var groups = new GraphClusterSet([0], new IReadOnlyList<int>[] { new[] { 0 } }, 0.5);
        Action action = () => GraphClusterPlacementOptimizer.Improve(profile, groups, [new(0, 0)], new CancellationToken(true));

        action.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Moving_cards_never_trades_drawn_crossings_or_hidden_arrows_for_shorter_lines()
    {
        var profile = GraphLayoutProfile.Create([100d, 180d, 120d, 160d, 130d, 200d],
            [(3, 0), (4, 1), (5, 2), (4, 0), (5, 1), (2, 0)]);
        var groups = new GraphClusterSet([0, 0, 0, 1, 1, 1],
            new IReadOnlyList<int>[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } }, 0.5);
        GraphPoint[] start = [new(0, 0), new(400, 0), new(800, 0), new(800, 1500), new(400, 1500), new(0, 1500)];
        var before = Drawn(profile, Cards(profile, start));

        var positions = GraphClusterPlacementOptimizer.Improve(profile, groups, start);
        var after = Drawn(profile, Cards(profile, positions));

        after.Hidden.Should().BeLessThanOrEqualTo(before.Hidden);
        after.Crossings.Should().BeLessThanOrEqualTo(before.Crossings);
        after.Length.Should().BeLessThanOrEqualTo(before.Length + 1e-6);
        after.Longest.Should().BeLessThanOrEqualTo(before.Longest + 1e-6);
        Separated(profile, groups, positions).Should().BeTrue();
    }

    [Fact]
    public void An_expired_request_returns_the_starting_placement_without_descent()
    {
        var profile = GraphLayoutProfile.Create([100d, 100d], [(1, 0)]);
        var groups = new GraphClusterSet([0, 1], new IReadOnlyList<int>[] { new[] { 0 }, new[] { 1 } }, 0.5);
        GraphPoint[] start = [new(0, 0), new(3000, 2000)];

        GraphClusterPlacementOptimizer.Improve(profile, groups, start, timeLeft: () => TimeSpan.Zero)
            .Should().Equal(start);
    }

    [Fact]
    public void Final_crossing_refinement_keeps_the_separated_group_gaps_and_drawn_quality()
    {
        var edges = new List<(int Child, int Parent)>();
        for (var group = 0; group < 2; group++)
            for (var first = 0; first < 6; first++)
                for (var second = first + 1; second < 6; second++)
                    edges.Add((group * 6 + first, group * 6 + second));
        edges.Add((0, 6));
        var profile = GraphLayoutProfile.Create(Enumerable.Repeat(140d, 12).ToArray(), edges);
        var budget = new GraphEvaluationBudget(1000);
        var draft = GraphClusteredSeed.Place(profile, default, budget, null)!;
        var repair = new GraphLayoutRepair();
        var before = repair.Repair(draft, profile, budget, hunt: false).Candidate!;

        var contested = repair.ReduceCrossingsByMoving(before, profile, budget);
        var after = repair.BeautifyCrossings(contested, profile, budget);

        profile.ClusterGroups.Should().HaveCount(2);
        GraphLayoutScore.MeasureClusterOverlap(profile, after.Layout.Nodes, GraphPlacementConstants.ClusterGap)
            .Should().BeApproximately(0, 1e-6);
        GraphLayoutScore.MeasureHiddenRelations(profile, after.Layout.Nodes, after.Routes)
            .Should().BeLessThanOrEqualTo(GraphLayoutScore.MeasureHiddenRelations(profile, before.Layout.Nodes, before.Routes));
        after.CrossingCount.Should().BeLessThanOrEqualTo(before.CrossingCount);
        GraphLayoutScore.Of(after, profile, 0).TotalLength.Should()
            .BeLessThanOrEqualTo(GraphLayoutScore.Of(before, profile, 0).TotalLength + 1e-6);
    }

    [Fact]
    public void Independent_branches_exclude_the_hub_and_external_community_members()
    {
        var profile = GraphLayoutProfile.Create(Enumerable.Repeat(100d, 8).ToArray(),
            [(1, 0), (2, 1), (3, 0), (4, 3), (5, 0), (6, 0), (6, 6), (2, 7)]);
        var groups = new GraphClusterSet([0, 0, 0, 0, 0, 0, 0, 1],
            new IReadOnlyList<int>[] { new[] { 0, 1, 2, 3, 4, 5, 6 }, new[] { 7 } }, 0);

        var branches = GraphStarPlacementMoves.Branches(profile, groups, 0);

        branches.Should().HaveCount(4);
        branches[0].Should().Equal(1, 2);
        branches[1].Should().Equal(3, 4);
        branches[2].Should().Equal(5);
        branches[3].Should().Equal(6);
        branches.SelectMany(branch => branch).Should().OnlyHaveUniqueItems().And.NotContain(0).And.NotContain(7);
    }

    [Fact]
    public void Geometric_blocker_sidesteps_can_clear_a_stroke_even_when_the_incident_arrow_gets_longer()
    {
        var profile = GraphLayoutProfile.Create([100d, 100d, 100d], [(1, 0), (2, 0)]);
        GraphPoint[] points = [new(0, 0), new(0, 300), new(0, 600)];
        var cards = Cards(profile, points);
        var before = Drawn(profile, cards);
        before.Hidden.Should().Be(1);

        var moves = GraphStarPlacementMoves.Clearances(profile, cards, 1).ToArray();

        moves.Should().NotBeEmpty();
        moves.Any(delta =>
        {
            var trial = cards.ToArray();
            trial[1] = trial[1] with { TopLeft = new GraphPoint(points[1].X + delta.X, points[1].Y + delta.Y) };
            var after = Drawn(profile, trial);
            return after.Hidden == 0 && after.Crossings <= before.Crossings && after.Length > before.Length;
        }).Should().BeTrue();
        cards[1].TopLeft.Should().Be(points[1]);
    }

    [Fact]
    public void Several_cards_blocking_one_star_spoke_can_be_cleared_without_moving_the_hub()
    {
        var profile = GraphLayoutProfile.Create([100d, 100d, 100d, 100d], [(1, 0), (2, 0), (3, 0)]);
        var groups = new GraphClusterSet([0, 0, 0, 0], new IReadOnlyList<int>[] { new[] { 0, 1, 2, 3 } }, 0);
        GraphPoint[] points = [new(0, 0), new(0, 200), new(0, 400), new(0, 800)];
        var before = Drawn(profile, Cards(profile, points));
        before.Hidden.Should().Be(2);

        var result = GraphClusterPlacementOptimizer.Improve(profile, groups, points, hub: 0);
        var after = Drawn(profile, Cards(profile, result));

        result[0].Should().Be(points[0]);
        after.Hidden.Should().Be(0);
        after.Crossings.Should().BeLessThanOrEqualTo(before.Crossings);
        GraphClusterPlacementOptimizer.Improve(profile, groups, points, hub: 0).Should().Equal(result);
        GraphPortAssigner.Assign(Cards(profile, result), profile.Edges).Ends
            .Select(end => end!.Parent.Point).Should().OnlyHaveUniqueItems();
    }

    private static (int Hidden, int Crossings, double Length, double Longest) Drawn(
        GraphLayoutProfile profile, IReadOnlyList<NodePlacement> cards)
    {
        var ports = GraphPortAssigner.Assign(cards, profile.Edges);
        ports.IsComplete.Should().BeTrue();
        var routes = ports.Ends.Select((end, edge) => new EdgeRoute(edge, EEdgeShape.Line, EEdgeGeometry.Straight,
            new[] { end!.Child.Point, end.Parent.Point }, end.Child.Point)).ToArray();
        var crossings = 0;
        for (var first = 0; first < routes.Length; first++)
            for (var second = first + 1; second < routes.Length; second++)
                if (GraphGeometry.SegmentsCross(routes[first].Points[0], routes[first].Points[1],
                    routes[second].Points[0], routes[second].Points[1])) crossings++;
        var lengths = routes.Select(route => GraphGeometry.ArcLength(route.Points)).ToArray();
        return (GraphLayoutScore.MeasureHiddenRelations(profile, cards, routes), crossings, lengths.Sum(), lengths.Max());
    }

    private static NodePlacement[] Cards(GraphLayoutProfile profile, IReadOnlyList<GraphPoint> positions) =>
        positions.Select((point, node) => new NodePlacement(node, point, profile.Widths[node], GraphCard.Height)).ToArray();

    private static double Length(GraphLayoutProfile profile, IReadOnlyList<GraphPoint> positions)
    {
        var cards = Cards(profile, positions);
        return profile.Edges.Sum(edge => GraphGeometry.Distance(cards[edge.Child].Centre, cards[edge.Parent].Centre));
    }

    private static bool Separated(GraphLayoutProfile profile, GraphClusterSet groups, IReadOnlyList<GraphPoint> positions)
    {
        var cards = Cards(profile, positions);
        for (var a = 0; a < groups.Members.Count; a++)
        {
            for (var b = a + 1; b < groups.Members.Count; b++)
            {
                var first = groups.Members[a];
                var second = groups.Members[b];
                var gap = GraphPlacementConstants.ClusterGap - 1e-6;
                if (first.Min(node => cards[node].TopLeft.X) < second.Max(node => cards[node].Right) + gap
                    && second.Min(node => cards[node].TopLeft.X) < first.Max(node => cards[node].Right) + gap
                    && first.Min(node => cards[node].TopLeft.Y) < second.Max(node => cards[node].Bottom) + gap
                    && second.Min(node => cards[node].TopLeft.Y) < first.Max(node => cards[node].Bottom) + gap)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
