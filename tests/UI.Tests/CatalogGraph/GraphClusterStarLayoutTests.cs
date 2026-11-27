using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

[Trait("Speed", "Fast")]
public sealed class GraphClusterStarLayoutTests
{
    [Fact]
    public void A_shared_hub_is_surrounded_by_separate_communities_not_ranked_above_them()
    {
        var profile = Fixture();
        var star = GraphClusterStarLayout.Detect(profile);
        star.Should().NotBeNull();
        star!.Value.Hub.Should().Be(0);

        var draft = GraphClusteredSeed.Place(profile, default, new GraphEvaluationBudget(5000), null)!;

        AssertSurrounded(profile, draft.Nodes);
        GraphLayoutScore.MeasureClusterOverlap(profile, draft.Nodes, GraphPlacementConstants.ClusterGap)
            .Should().BeApproximately(0, 1e-6);
        draft.Nodes.Should().HaveCount(profile.NodeCount);
    }

    [Fact]
    public void Single_connection_tables_use_both_sides_and_both_heights_around_their_hub()
    {
        var profile = Fixture();
        var draft = GraphClusteredSeed.Place(profile, default, new GraphEvaluationBudget(5000), null)!;
        var hub = draft.Nodes[0].Centre;
        var leaves = new[] { 1, 2, 3, 4 }.Select(node => draft.Nodes[node].Centre).ToArray();

        leaves.Should().Contain(point => point.X < hub.X);
        leaves.Should().Contain(point => point.X > hub.X);
        leaves.Should().Contain(point => point.Y < hub.Y);
        leaves.Should().Contain(point => point.Y > hub.Y);
    }

    [Fact]
    public void Refinement_keeps_the_star_and_every_constraint_has_distinct_border_ports()
    {
        var profile = Fixture();
        var budget = new GraphEvaluationBudget(5000);
        var draft = GraphClusteredSeed.Place(profile, default, budget, null)!;
        var repair = new GraphLayoutRepair();
        var outcome = repair.Repair(draft, profile, budget, hunt: false);
        outcome.IsSuccess.Should().BeTrue();
        var before = outcome.Candidate!;

        var contested = repair.ReduceCrossingsByMoving(before, profile, budget);
        var after = repair.BeautifyCrossings(contested, profile, budget);

        AssertSurrounded(profile, after.Layout.Nodes);
        after.Routes.Should().HaveCount(profile.Edges.Count);
        GraphLayoutScore.MeasureClusterOverlap(profile, after.Layout.Nodes, GraphPlacementConstants.ClusterGap)
            .Should().BeApproximately(0, 1e-6);
        foreach (var group in after.Ends.SelectMany((ends, edge) => new[]
        {
            (Node: profile.Edges[edge].Child, ends.Child.Point),
            (Node: profile.Edges[edge].Parent, ends.Parent.Point)
        }).GroupBy(port => port.Node))
            group.Select(port => port.Point).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_chain_of_communities_does_not_acquire_a_false_star_hub()
    {
        var edges = Enumerable.Range(1, 24).Select(node => (node, node - 1)).ToArray();
        var profile = GraphLayoutProfile.Create(Enumerable.Repeat(100d, 25).ToArray(), edges);

        GraphClusterStarLayout.Detect(profile).Should().BeNull();
    }

    [Fact]
    public void Reversing_foreign_key_direction_does_not_put_the_star_hub_at_the_top()
    {
        var profile = Fixture();
        var reversed = GraphLayoutProfile.Create(profile.Widths, profile.Edges.Select(edge => (edge.Parent, edge.Child)).ToArray());
        var draft = GraphClusteredSeed.Place(reversed, default, new GraphEvaluationBudget(5000), null)!;

        GraphClusterStarLayout.Detect(reversed)!.Value.Hub.Should().Be(0);
        AssertSurrounded(reversed, draft.Nodes);
    }

    [Fact]
    public void Whole_picture_translation_is_allowed_but_moving_the_hub_is_not()
    {
        var profile = Fixture();
        var topology = GraphClusterStarLayout.Detect(profile)!.Value;
        var draft = GraphClusteredSeed.Place(profile, default, new GraphEvaluationBudget(5000), null)!;
        var translated = draft.Nodes.Select(card => card with
        {
            TopLeft = new GraphPoint(card.TopLeft.X + 123, card.TopLeft.Y + 456)
        }).ToArray();

        GraphClusterStarLayout.Preserves(draft.Nodes, translated, topology.Clusters, topology.Hub).Should().BeTrue();
        translated[0] = translated[0] with { TopLeft = new GraphPoint(translated[0].TopLeft.X + 100, translated[0].TopLeft.Y) };
        GraphClusterStarLayout.Preserves(draft.Nodes, translated, topology.Clusters, topology.Hub).Should().BeFalse();
    }

    [Fact]
    public void Choosing_a_hub_does_not_change_an_unrelated_components_local_arrangement()
    {
        var edges = new[] { (1, 0), (2, 0), (3, 0), (4, 0), (6, 5), (7, 5), (8, 5), (9, 5) };
        var profile = GraphLayoutProfile.Create(Enumerable.Repeat(140d, 10).ToArray(), edges);
        var ordinary = GraphRadialSeed.Place(profile);

        var star = GraphRadialSeed.Place(profile, 0);

        for (var node = 6; node < 10; node++)
        {
            (star.Nodes[node].Centre.X - star.Nodes[5].Centre.X).Should()
                .BeApproximately(ordinary.Nodes[node].Centre.X - ordinary.Nodes[5].Centre.X, 1e-6);
            (star.Nodes[node].Centre.Y - star.Nodes[5].Centre.Y).Should()
                .BeApproximately(ordinary.Nodes[node].Centre.Y - ordinary.Nodes[5].Centre.Y, 1e-6);
        }
    }

    [Fact]
    public void Moving_most_tables_is_not_mistaken_for_a_translation_of_the_pinned_hub()
    {
        var (groups, origin) = SmallStar();
        var trial = origin.Select(card => card.NodeIndex == 0 ? card : card with
        {
            TopLeft = new GraphPoint(card.TopLeft.X + 40, card.TopLeft.Y + 40)
        }).ToArray();

        GraphClusterStarLayout.Preserves(origin, trial, groups, 0).Should().BeTrue();
        trial[0].Centre.Should().Be(origin[0].Centre);
    }

    [Fact]
    public void A_leaf_can_change_sector_while_the_family_and_satellites_still_surround_the_hub()
    {
        var (groups, origin) = SmallStar();
        var trial = origin.ToArray();
        trial[1] = trial[1] with { TopLeft = new GraphPoint(50, -320) };

        GraphClusterStarLayout.Preserves(origin, trial, groups, 0).Should().BeTrue();
        foreach (var group in groups.Members.Skip(1))
            foreach (var node in group) trial[node].Should().Be(origin[node]);
    }

    [Fact]
    public void Gathering_all_satellites_on_one_side_is_still_rejected()
    {
        var (groups, origin) = SmallStar();
        var trial = origin.ToArray();
        foreach (var node in groups.Members.Skip(1).SelectMany(group => group))
            trial[node] = trial[node] with { TopLeft = new GraphPoint(node * 200, 1000) };

        GraphClusterStarLayout.Preserves(origin, trial, groups, 0).Should().BeFalse();
    }

    [Fact]
    public void Empty_space_between_independent_hub_branches_is_not_an_occupied_community_box()
    {
        var profile = Fixture();
        var points = new GraphPoint[profile.NodeCount];
        points[0] = new(0, 0);
        points[1] = new(1800, 0);
        points[2] = new(0, 1800);
        points[3] = new(-1800, 0);
        points[4] = new(0, -1800);
        for (var group = 0; group < 4; group++)
            for (var at = 0; at < 5; at++)
                points[5 + group * 5 + at] = new GraphPoint((group < 2 ? -1500 : 800) + at * 170, group % 2 == 0 ? -800 : 800);
        var cards = points.Select((point, node) => new NodePlacement(node, point, profile.Widths[node], GraphCard.Height)).ToArray();

        profile.ClusterGroups.Should().HaveCount(5);
        profile.ClusterRegions.Should().HaveCount(9);
        profile.ClusterRegions.SelectMany(region => region.Members).Should().OnlyHaveUniqueItems().And.HaveCount(profile.NodeCount);
        GraphLayoutScore.MeasureClusterOverlap(profile, cards, GraphPlacementConstants.ClusterGap).Should().BeApproximately(0, 1e-6);
        cards[5] = cards[5] with { TopLeft = cards[1].TopLeft };
        GraphLayoutScore.MeasureClusterOverlap(profile, cards, GraphPlacementConstants.ClusterGap).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Star_regions_are_mapped_independently_for_multiple_connected_components()
    {
        var first = Fixture();
        var profile = GraphLayoutProfile.Create(first.Widths.Concat(first.Widths).ToArray(),
            first.Edges.Concat(first.Edges.Select(edge => (edge.Child + first.NodeCount, edge.Parent + first.NodeCount))).ToArray());

        profile.ClusterRegions.Should().HaveCount(18);
        profile.ClusterRegions.SelectMany(region => region.Members).Should().OnlyHaveUniqueItems().And.HaveCount(profile.NodeCount);
        foreach (var region in profile.ClusterRegions)
            region.Members.Select(node => profile.Component[node]).Distinct().Should().ContainSingle();
        profile.ClusterRegions.Should().BeSameAs(profile.ClusterRegions);
    }

    private static (GraphClusterSet Groups, NodePlacement[] Cards) SmallStar()
    {
        var groups = new GraphClusterSet([0, 0, 0, 0, 0, 1, 2, 3, 4],
            new IReadOnlyList<int>[] { new[] { 0, 1, 2, 3, 4 }, new[] { 5 }, new[] { 6 }, new[] { 7 }, new[] { 8 } }, 0);
        GraphPoint[] centres = [new(0, 0), new(200, 0), new(0, 200), new(-200, 0), new(0, -200),
            new(600, 0), new(0, 600), new(-600, 0), new(0, -600)];
        return (groups, centres.Select((point, node) => new NodePlacement(node, new GraphPoint(point.X - 50, point.Y - 20), 100, 40)).ToArray());
    }

    private static void AssertSurrounded(GraphLayoutProfile profile, IReadOnlyList<NodePlacement> cards)
    {
        var root = GraphClusterStarLayout.Detect(profile)!.Value.Hub;
        var hub = cards[root].Centre;
        var satellites = profile.ClusterGroups.Where(group => !group.Contains(root))
            .Select(group => new GraphPoint(group.Average(node => cards[node].Centre.X), group.Average(node => cards[node].Centre.Y)))
            .ToArray();
        satellites.Should().Contain(point => point.X < hub.X);
        satellites.Should().Contain(point => point.X > hub.X);
        satellites.Should().Contain(point => point.Y < hub.Y);
        satellites.Should().Contain(point => point.Y > hub.Y);
    }

    private static GraphLayoutProfile Fixture()
    {
        var edges = new List<(int Child, int Parent)> { (1, 0), (2, 0), (3, 0), (4, 0) };
        for (var group = 0; group < 4; group++)
        {
            var first = 5 + group * 5;
            edges.Add((first, 0));
            for (var from = first; from < first + 5; from++)
                for (var to = from + 1; to < first + 5; to++) edges.Add((from, to));
        }
        return GraphLayoutProfile.Create(Enumerable.Repeat(140d, 25).ToArray(), edges);
    }
}
