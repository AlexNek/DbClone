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
