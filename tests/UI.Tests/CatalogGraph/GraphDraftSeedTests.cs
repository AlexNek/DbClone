using System.Globalization;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks the continuous draft seeds (refactor 14, milestone D).</summary>
public sealed class GraphDraftSeedTests
{
    public static TheoryData<string> Fixtures => new() { "chain", "hub", "cycle", "components", "dense" };

    // The component clears MinimumClusteredComponent and its modularity clears MinimumClusterModularity, so the
    // clustered seed joins the five plain drafts; every other fixture is laid out exactly as before refactor 18.
    private static string[] ExpectedNames(string fixture) =>
        fixture is "chain" or "dense"
            ? ["ranked", "radial", "force", "force-wide", "force-transposed", "clustered"]
            : ["ranked", "radial", "force", "force-wide", "force-transposed"];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_seed_places_every_card_finite_and_apart_inside_its_canvas(string fixture)
    {
        var profile = Profile(fixture);

        var drafts = GraphDraftSeeds.Generate(profile);

        drafts.Select(draft => draft.Name).Should().Equal(ExpectedNames(fixture));
        foreach (var draft in drafts)
        {
            draft.Nodes.Should().HaveCount(profile.NodeCount, draft.Name);
            foreach (var node in draft.Nodes)
            {
                double.IsFinite(node.TopLeft.X).Should().BeTrue(draft.Name);
                double.IsFinite(node.TopLeft.Y).Should().BeTrue(draft.Name);
                node.TopLeft.X.Should().BeGreaterThanOrEqualTo(GraphPlacementConstants.Margin - 1e-6, draft.Name);
                node.TopLeft.Y.Should().BeGreaterThanOrEqualTo(GraphPlacementConstants.Margin - 1e-6, draft.Name);
                node.Right.Should().BeLessThanOrEqualTo(draft.Width, draft.Name);
                node.Bottom.Should().BeLessThanOrEqualTo(draft.Height, draft.Name);
            }

            Overlaps(draft).Should().BeEmpty($"{draft.Name} must not stack two cards");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_same_graph_gives_the_same_coordinates_under_any_culture(string fixture)
    {
        var first = Signature(GraphDraftSeeds.Generate(Profile(fixture)));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Signature(GraphDraftSeeds.Generate(Profile(fixture))).Should().Be(first);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_ranked_seed_stands_every_child_below_the_parent_it_points_at_in_a_chain()
    {
        var draft = GraphRankedSeed.Place(Profile("chain"));

        for (var node = 1; node < draft.Nodes.Count; node++)
        {
            draft.Nodes[node].Centre.Y.Should().BeGreaterThan(draft.Nodes[node - 1].Centre.Y);
        }
    }

    [Fact]
    public void Components_that_share_no_relation_do_not_overlap_and_a_cycle_shares_one_rank()
    {
        var profile = Profile("components");
        profile.ComponentNodes.Count.Should().Be(3);

        var cycle = GraphLayoutProfile.Create([100, 100, 100], [(0, 1), (1, 2), (2, 0)]);
        cycle.Rank.Should().Equal(0, 0, 0);
        cycle.GroupSize.Should().Equal(3, 3, 3);
    }

    [Fact]
    public void A_thousand_table_chain_is_seeded_without_recursion_and_within_the_budget()
    {
        var widths = Enumerable.Repeat(100.0, 1000).ToArray();
        var edges = Enumerable.Range(1, 999).Select(node => (node, node - 1)).ToArray();

        var drafts = GraphDraftSeeds.Generate(GraphLayoutProfile.Create(widths, edges));

        // A 1000-table chain splits into 31 clusters (Phase 0), inside MaximumClusteredClusters, so the clustered
        // seed is generated: six drafts, not five.
        drafts.Should().HaveCount(6);
        drafts.All(draft => Overlaps(draft).Count == 0).Should().BeTrue();
    }

    [Fact]
    public void A_cancelled_request_stops_with_a_cancellation_and_no_partial_draft()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var act = () => GraphDraftSeeds.Generate(Profile("dense"), source.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    private static GraphLayoutProfile Profile(string fixture)
    {
        var widths = Enumerable.Range(0, Count(fixture)).Select(node => 72.0 + ((node * 13) % 90)).ToArray();

        return GraphLayoutProfile.Create(widths, Edges(fixture));
    }

    private static int Count(string fixture) => fixture switch
    {
        "chain" => 12,
        "hub" => 15,
        "cycle" => 8,
        "components" => 9,
        _ => 20,
    };

    private static (int Child, int Parent)[] Edges(string fixture) => fixture switch
    {
        "chain" => Enumerable.Range(1, 11).Select(node => (node, node - 1)).ToArray(),
        "hub" => Enumerable.Range(1, 14).Select(node => (node, 0)).ToArray(),
        "cycle" => [(0, 1), (1, 2), (2, 3), (3, 0), (4, 0), (5, 4), (6, 5), (7, 7), (7, 6), (2, 6)],
        "components" => [(1, 0), (2, 0), (4, 3), (5, 3), (7, 6), (8, 6), (8, 7)],
        _ => Enumerable.Range(0, 20).SelectMany(node => new[] { (node, (node + 3) % 20), (node, (node + 7) % 20) }).ToArray(),
    };

    private static List<string> Overlaps(GraphDraftLayout draft)
    {
        var found = new List<string>();
        for (var a = 0; a < draft.Nodes.Count; a++)
        {
            for (var b = a + 1; b < draft.Nodes.Count; b++)
            {
                var (p, q) = (draft.Nodes[a], draft.Nodes[b]);
                if (p.TopLeft.X < q.Right && q.TopLeft.X < p.Right && p.TopLeft.Y < q.Bottom && q.TopLeft.Y < p.Bottom)
                {
                    found.Add($"{a}/{b}");
                }
            }
        }

        return found;
    }

    private static string Signature(IEnumerable<GraphDraftLayout> drafts) =>
        string.Join(
            "|",
            drafts.Select(draft => draft.Name + ":" + string.Join(
                ",",
                draft.Nodes.Select(node => node.TopLeft.X.ToString("R", CultureInfo.InvariantCulture) + " " + node.TopLeft.Y.ToString("R", CultureInfo.InvariantCulture)))));
}
