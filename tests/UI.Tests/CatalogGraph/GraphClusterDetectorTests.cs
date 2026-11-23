using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Refactor 18 Phase 0: what <see cref="GraphClusterDetector"/> does, measured before anything is drawn. The
/// hand-written cases are the plan's D1/Phase-0 acceptance graphs; the per-fixture pins are the numbers the
/// committed detector produces (multi-level Louvain), recorded so a later change to the partition is a
/// deliberate edit here rather than a silent drift. This is the gate every later phase leans on.
/// </summary>
public sealed class GraphClusterDetectorTests
{
    [Fact]
    public void Two_six_table_cliques_joined_by_one_relation_split_into_two_clusters()
    {
        var profile = ProfileOfTwoCliques();
        var clustered = DetectComponent(profile, 0);

        clustered.Should().NotBeNull("the split clears every guard");
        clustered!.Members.Should().HaveCount(2);
        clustered.Members.Select(cluster => cluster.Count).OrderBy(count => count).Should().Equal(6, 6);
        clustered.Modularity.Should().BeGreaterThan(GraphSearchBudget.MinimumClusterModularity);
    }

    [Fact]
    public void A_graph_below_the_component_guard_is_never_clustered()
    {
        // Ten tables in a chain: under MinimumClusteredComponent, so the component is laid out as today.
        var edges = Enumerable.Range(1, 9).Select(node => (node, node - 1)).ToArray();
        var profile = GraphQualityFixtures.Profile(10, edges);

        DetectComponent(profile, 0).Should().BeNull();
    }

    [Fact]
    public void A_star_whose_split_is_not_real_structure_stays_one_cluster()
    {
        // The hub fixture (15 tables, one centre) clears the size guard but not the modularity guard.
        DetectLargest(GraphQualityFixtures.Profile("hub")).Should().BeNull();
    }

    [Fact]
    public void The_detector_reports_no_cluster_smaller_than_the_merge_floor()
    {
        // Every reported cluster survives MergeSmallClusters, so none is under MinimumClusterSize.
        var clustered = DetectLargest(GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog));

        clustered.Should().NotBeNull();
        clustered!.Members.Should().OnlyContain(cluster => cluster.Count >= GraphSearchBudget.MinimumClusterSize);
    }

    [Fact]
    public void The_same_graph_gives_the_same_partition_on_every_run()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog);
        var first = DetectLargest(profile);

        first.Should().NotBeNull();
        for (var run = 0; run < 3; run++)
        {
            var again = DetectLargest(profile);
            again!.ClusterOf.Should().Equal(first!.ClusterOf, $"run {run}");
            again.Modularity.Should().Be(first.Modularity, $"run {run}");
        }
    }

    [Fact]
    public void Clusters_are_numbered_from_their_lowest_node_and_members_are_ascending()
    {
        var clustered = DetectLargest(GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog));
        clustered.Should().NotBeNull();

        for (var cluster = 0; cluster < clustered!.Members.Count; cluster++)
        {
            clustered.Members[cluster].Should().BeInAscendingOrder($"cluster {cluster} members");
        }

        // Cluster 0 holds the component's lowest node; numbers follow first appearance.
        clustered.Members[0].Should().Contain(0);
    }

    [Theory]
    [InlineData("catalog", 7, 0.5567)]
    [InlineData("clusters", 4, 0.6053)]
    [InlineData("ring", 10, 0.7600)]
    [InlineData("plaza", 2, 0.3438)]
    [InlineData("chain", 3, 0.4835)]
    [InlineData("scale", 31, 0.9377)]
    public void Each_clustered_fixture_splits_into_the_pinned_clusters(string fixture, int clusters, double modularity)
    {
        var clustered = DetectLargest(GraphQualityFixtures.Profile(fixture));

        clustered.Should().NotBeNull($"{fixture} clears the guards");
        clustered!.Members.Count.Should().Be(clusters, $"{fixture} cluster count");
        clustered.Modularity.Should().BeApproximately(modularity, 0.002, $"{fixture} modularity");
    }

    [Theory]
    [InlineData("hub")]
    [InlineData("dense")]
    [InlineData("cycle")]
    [InlineData("components")]
    [InlineData("unobstructed")]
    [InlineData("lightly-obstructed")]
    public void Every_guard_rejected_fixture_is_not_clustered(string fixture)
    {
        DetectLargest(GraphQualityFixtures.Profile(fixture)).Should().BeNull($"{fixture} is laid out as today");
    }

    /// <summary>Detects on one named component, given exactly the indexes the clustered seed hands the detector.</summary>
    private static GraphClusterSet? DetectComponent(GraphLayoutProfile profile, int component)
    {
        var sub = profile.Subgraph(component);
        var nodes = Enumerable.Range(0, sub.Nodes.Count).ToArray();
        var edges = Enumerable.Range(0, sub.Edges.Count).ToArray();
        return GraphClusterDetector.Detect(sub.Profile, nodes, edges);
    }

    /// <summary>Detects on the largest connected component of a profile; null when that component is under the guard.</summary>
    private static GraphClusterSet? DetectLargest(GraphLayoutProfile profile)
    {
        var largest = 0;
        for (var component = 1; component < profile.ComponentNodes.Count; component++)
        {
            if (profile.ComponentNodes[component].Count > profile.ComponentNodes[largest].Count)
            {
                largest = component;
            }
        }

        return DetectComponent(profile, largest);
    }

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
