using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Refactor 18 Phase 2: how <see cref="GraphClusterArranger"/> and <see cref="GraphClusteredSeed"/> place the
/// cluster boxes — no two boxes share ground on a clustered fixture, the catalog's centre cluster holds the
/// table most relations meet, and mirroring a box keeps every distance inside the cluster it mirrors.
/// </summary>
public sealed class GraphClusterArrangerTests
{
    // The fixtures Phase 0 admits for clustering that are small enough to place in a fast test.
    [Theory]
    [InlineData("chain")]
    [InlineData("plaza")]
    [InlineData("clusters")]
    [InlineData("ring")]
    [InlineData("catalog")]
    public void No_two_cluster_boxes_share_ground_on_a_clustered_fixture(string fixture)
    {
        var profile = GraphQualityFixtures.Profile(fixture);
        var draft = GraphClusteredSeed.Place(profile, default, new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations), null);

        draft.Should().NotBeNull($"{fixture} is clustered");
        GraphLayoutScore.MeasureClusterOverlap(profile, draft!.Nodes)
            .Should().BeApproximately(0, 1e-6, $"{fixture}: separated clusters never overlap");
    }

    [Fact]
    public void The_catalog_centre_cluster_holds_the_table_most_relations_meet()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog);
        var (component, sub) = LargestComponent(profile);
        var nodes = Enumerable.Range(0, sub.Nodes.Count).ToArray();
        var edges = Enumerable.Range(0, sub.Edges.Count).ToArray();
        var clustered = GraphClusterDetector.Detect(sub.Profile, nodes, edges);
        clustered.Should().NotBeNull("the catalog's large component is clustered");

        // The busiest table: the one the most relation ends meet, counted over the whole catalog.
        var ends = new int[profile.NodeCount];
        foreach (var (child, parent) in profile.Edges)
        {
            ends[child]++;
            ends[parent]++;
        }

        var busiestGlobal = 0;
        for (var node = 1; node < ends.Length; node++)
        {
            if (ends[node] > ends[busiestGlobal])
            {
                busiestGlobal = node;
            }
        }

        component.Should().Contain(busiestGlobal, "the busiest table is in the component that is clustered");
        var local = System.Array.IndexOf(sub.Nodes.ToArray(), busiestGlobal);
        local.Should().BeGreaterThanOrEqualTo(0);
        var busiestCluster = clustered!.ClusterOf[local];

        // The centre cluster is the one with the greatest summed weight of relations reaching other clusters
        // (ties: the lowest cluster number) — the same rule D3 arranges around.
        var centre = CentreCluster(clustered, sub.Profile);
        busiestCluster.Should().Be(centre, "the busiest table's cluster is the arrangement's centre");
    }

    [Fact]
    public void Mirroring_a_cluster_box_leaves_every_distance_inside_it_unchanged()
    {
        var widths = Enumerable.Repeat(100d, 6).ToArray();
        var edges = new[] { (1, 0), (2, 1), (3, 0) };
        var profile = GraphLayoutProfile.Create(widths, edges);
        var clustered = new GraphClusterSet(
            [0, 0, 0, 1, 1, 1],
            [new[] { 0, 1, 2 }, new[] { 3, 4, 5 }],
            0.5);
        var locals = new[]
        {
            new GraphLocalLayout(new[] { 0, 1, 2 }, [new GraphPoint(0, 0), new GraphPoint(120, 0), new GraphPoint(0, 60)], 220, 100),
            new GraphLocalLayout(new[] { 3, 4, 5 }, [new GraphPoint(0, 0), new GraphPoint(120, 0), new GraphPoint(60, 60)], 220, 100),
        };

        var (offsets, flips, _, _) = GraphClusterArranger.Arrange(clustered, locals, profile);

        for (var cluster = 0; cluster < locals.Length; cluster++)
        {
            var local = locals[cluster];
            var final = local.TopLefts.Select((point, at) => Mirrored(point, local, flips[cluster], profile.Widths[local.Nodes[at]], offsets[cluster])).ToArray();
            for (var a = 0; a < final.Length; a++)
            {
                for (var b = a + 1; b < final.Length; b++)
                {
                    GraphGeometry.Distance(final[a], final[b])
                        .Should().BeApproximately(GraphGeometry.Distance(local.TopLefts[a], local.TopLefts[b]), 1e-6, $"cluster {cluster} keeps its internal shape under a mirror");
                }
            }
        }
    }

    private static GraphPoint Mirrored(GraphPoint topLeft, GraphLocalLayout local, (bool Horizontal, bool Vertical) flip, double width, GraphPoint offset)
    {
        var x = flip.Horizontal ? local.Width - (topLeft.X + width) : topLeft.X;
        var y = flip.Vertical ? local.Height - (topLeft.Y + GraphCard.Height) : topLeft.Y;
        return new GraphPoint(offset.X + x, offset.Y + y);
    }

    private static int CentreCluster(GraphClusterSet clustered, GraphLayoutProfile sub)
    {
        var weight = new int[clustered.Members.Count];
        foreach (var (child, parent) in sub.Edges)
        {
            var from = clustered.ClusterOf[child];
            var to = clustered.ClusterOf[parent];
            if (from != to)
            {
                weight[from]++;
                weight[to]++;
            }
        }

        var centre = 0;
        for (var cluster = 1; cluster < weight.Length; cluster++)
        {
            if (weight[cluster] > weight[centre])
            {
                centre = cluster;
            }
        }

        return centre;
    }

    private static (IReadOnlyList<int> Nodes, GraphSubgraph Sub) LargestComponent(GraphLayoutProfile profile)
    {
        var largest = 0;
        for (var component = 1; component < profile.ComponentNodes.Count; component++)
        {
            if (profile.ComponentNodes[component].Count > profile.ComponentNodes[largest].Count)
            {
                largest = component;
            }
        }

        return (profile.ComponentNodes[largest], profile.Subgraph(largest));
    }
}
