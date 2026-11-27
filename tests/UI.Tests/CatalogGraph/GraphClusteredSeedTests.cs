using System.Globalization;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Refactor 18 Phase 1: what <see cref="GraphClusteredSeed"/> does once a component is admitted for
/// clustering — every cluster gets its own positions, the nested per-cluster contest never re-runs the
/// clustered maker, the placement is deterministic, and the cluster work is charged to the request's budget.
/// The two 6-table cliques joined by one relation is the smallest graph that clears both clustering guards.
/// </summary>
public sealed class GraphClusteredSeedTests
{
    [Fact]
    public void Each_cluster_of_the_two_clique_graph_gets_its_own_positions()
    {
        var profile = ProfileOfTwoCliques();
        var clustered = DetectWhole(profile);
        clustered.Should().NotBeNull("the two-clique graph clears the size and modularity guards");
        clustered!.Members.Should().HaveCount(2);

        var draft = GraphClusteredSeed.Place(profile, default, new GraphEvaluationBudget(50_000), null);

        draft.Should().NotBeNull();
        draft!.Name.Should().Be("clustered");
        draft.Nodes.Should().HaveCount(profile.NodeCount, "every card of both clusters is placed");
        foreach (var node in draft.Nodes)
        {
            double.IsFinite(node.TopLeft.X).Should().BeTrue();
            double.IsFinite(node.TopLeft.Y).Should().BeTrue();
        }

        // Each detected cluster holds a box of its own: the two boxes never share ground.
        GraphLayoutScore.MeasureClusterOverlap(profile, draft.Nodes)
            .Should().BeApproximately(0, 1e-6, "a cluster is drawn as a group apart from the other");
    }

    [Fact]
    public void The_clustered_maker_is_left_out_when_a_cluster_selects_without_clustering()
    {
        var profile = ProfileOfTwoCliques();

        var nested = GraphDraftSeeds.Generate(profile, clustering: false);
        var whole = GraphDraftSeeds.Generate(profile, clustering: true);

        nested.Select(draft => draft.Name).Should().NotContain("clustered", "a cluster's own contest never re-runs the clustered maker");
        whole.Select(draft => draft.Name).Should().Contain("clustered");
    }

    [Fact]
    public void The_same_clustered_graph_is_placed_identically_across_three_runs()
    {
        var profile = ProfileOfTwoCliques();
        var first = Signature(GraphClusteredSeed.Place(profile, default, new GraphEvaluationBudget(50_000), null)!);

        for (var run = 0; run < 3; run++)
        {
            var again = GraphClusteredSeed.Place(profile, default, new GraphEvaluationBudget(50_000), null)!;
            Signature(again).Should().Be(first, $"run {run}");
        }
    }

    private static GraphClusterSet? DetectWhole(GraphLayoutProfile profile)
    {
        var sub = profile.Subgraph(0);
        var nodes = Enumerable.Range(0, sub.Nodes.Count).ToArray();
        var edges = Enumerable.Range(0, sub.Edges.Count).ToArray();
        return GraphClusterDetector.Detect(sub.Profile, nodes, edges);
    }

    private static string Signature(GraphDraftLayout draft) =>
        string.Join(
            ",",
            draft.Nodes.Select(node =>
                node.TopLeft.X.ToString("R", CultureInfo.InvariantCulture) + " " +
                node.TopLeft.Y.ToString("R", CultureInfo.InvariantCulture)));

    private static GraphLayoutProfile ProfileOfTwoCliques()
    {
        var edges = new List<(int Child, int Parent)>();
        void Clique(int offset)
        {
            for (var a = 0; a < 6; a++)
            {
                for (var b = a + 1; b < 6; b++)
                {
                    edges.Add((offset + a, offset + b));
                }
            }
        }

        Clique(0);
        Clique(6);
        edges.Add((0, 6));
        return GraphQualityFixtures.Profile(12, edges);
    }
}
