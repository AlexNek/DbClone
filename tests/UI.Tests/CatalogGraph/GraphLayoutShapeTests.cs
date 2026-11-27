using System.Diagnostics;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// The quick gate over catalogs nobody wrote down: a seeded generator draws a small schema, and only general
/// properties are asked of the answer — every card placed, no two cards on top of each other, most relations
/// routed, the same shape drawn the same way, and always some picture. Nothing here knows a fixture name, so a
/// change that only fits one catalog cannot pass it.
/// </summary>
[Trait("Speed", "Fast")]
public sealed class GraphLayoutShapeTests
{
    public static TheoryData<int> Seeds => new() { 1, 2, 3, 4 };

    [Theory]
    [MemberData(nameof(Seeds))]
    public void A_generated_catalog_is_placed_without_stacked_cards_and_routes_nearly_every_relation(int seed)
    {
        var profile = GraphQualityFixtures.Profile(Shapes.Nodes, Shapes.Edges(seed));

        var layout = GraphPlacementState.From(GraphRadialSeed.Place(profile), profile).Assemble("radial");

        layout.Nodes.Should().HaveCount(profile.NodeCount);
        Stacked(layout.Nodes).Should().BeEmpty();

        var ports = GraphPortAssigner.Assign(layout.Nodes, profile.Edges);
        ports.IsComplete.Should().BeTrue();

        var routing = new GraphNonOrthogonalRouter().Route(layout.Nodes, profile, ports, default, new GraphEvaluationBudget(20_000));

        routing.Failures.Should().HaveCountLessThanOrEqualTo(profile.Edges.Count / 10, "a seed routes nearly every relation; the rest is the repair's work");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void The_same_shape_is_always_drawn_the_same_way(int seed)
    {
        var profile = GraphQualityFixtures.Profile(Shapes.Nodes, Shapes.Edges(seed));

        var first = GraphPlacementState.From(GraphRadialSeed.Place(profile), profile).Assemble("radial").Nodes;
        var second = GraphPlacementState.From(GraphRadialSeed.Place(profile), profile).Assemble("radial").Nodes;

        first.Select(node => (node.NodeIndex, node.TopLeft, node.Right, node.Bottom))
            .Should().Equal(second.Select(node => (node.NodeIndex, node.TopLeft, node.Right, node.Bottom)));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    [Trait("Speed", "Slow")]
    public void A_generated_catalog_always_ends_with_a_whole_picture(int seed)
    {
        var profile = GraphQualityFixtures.Profile(Shapes.Nodes, Shapes.Edges(seed));

        var watch = Stopwatch.StartNew();
        var outcome = new GraphLayoutSelector().Select(profile, timeLimit: TimeSpan.FromSeconds(5));
        watch.Stop();

        outcome.Best.Should().NotBeNull("a whole picture is worth more than a perfect one");
        outcome.Best!.Layout.Nodes.Should().HaveCount(profile.NodeCount);
        Stacked(outcome.Best.Layout.Nodes).Should().BeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    private static List<string> Stacked(IReadOnlyList<NodePlacement> nodes)
    {
        var found = new List<string>();
        for (var a = 0; a < nodes.Count; a++)
        {
            for (var b = a + 1; b < nodes.Count; b++)
            {
                var (p, q) = (nodes[a], nodes[b]);
                if (p.TopLeft.X < q.Right && q.TopLeft.X < p.Right && p.TopLeft.Y < q.Bottom && q.TopLeft.Y < p.Bottom)
                {
                    found.Add($"{p.NodeIndex}/{q.NodeIndex}");
                }
            }
        }

        return found;
    }
}
