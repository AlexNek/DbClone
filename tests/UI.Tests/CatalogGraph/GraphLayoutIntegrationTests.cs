using System.Diagnostics;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// The quick gate over one whole arrangement request: every small catalog and one mid-sized catalog of
/// settlements are seeded, routed and validated in about half a minute. Run this before the measured
/// characterization (<see cref="GraphLayoutQualityTests"/>), which is marked <c>Slow</c> and takes minutes.
/// </summary>
[Trait("Speed", "Fast")]
public sealed class GraphLayoutIntegrationTests
{
    public static TheoryData<string> Small => new() { "lightly-obstructed", "dense", "chain", "hub", "components", "cycle" };

    [Theory]
    [MemberData(nameof(Small))]
    public void The_radial_seed_routes_every_relation_of_a_small_catalog(string fixture)
    {
        var profile = GraphQualityFixtures.Profile(fixture);
        var layout = GraphPlacementState.From(GraphRadialSeed.Place(profile), profile).Assemble("radial");
        var ports = GraphPortAssigner.Assign(layout.Nodes, profile.Edges);
        ports.IsComplete.Should().BeTrue(fixture);

        var routing = new GraphNonOrthogonalRouter().Route(layout.Nodes, profile, ports, default, new GraphEvaluationBudget(20_000));

        routing.Failures.Should().BeEmpty(fixture);
    }

    [Theory]
    [MemberData(nameof(Small))]
    public void A_small_catalog_is_arranged_completely_and_without_stacked_cards(string fixture)
    {
        var profile = GraphQualityFixtures.Profile(fixture);
        var outcome = new GraphLayoutSelector().Select(profile, timeLimit: TimeSpan.FromSeconds(20));

        outcome.IsSuccess.Should().BeTrue(fixture);
        outcome.IsDegraded.Should().BeFalse(fixture);
        var candidate = outcome.Best!;
        candidate.Layout.Nodes.Should().HaveCount(profile.NodeCount, fixture);
        candidate.Routes.Should().HaveCount(profile.Edges.Count, fixture);
        Stacked(candidate.Layout.Nodes).Should().BeEmpty(fixture);
    }

    [Fact]
    public void The_radial_seed_routes_every_relation_of_a_catalog_of_settlements()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Plaza);
        var layout = GraphPlacementState.From(GraphRadialSeed.Place(profile), profile).Assemble("radial");
        var ports = GraphPortAssigner.Assign(layout.Nodes, profile.Edges);
        ports.IsComplete.Should().BeTrue();

        var routing = new GraphNonOrthogonalRouter().Route(layout.Nodes, profile, ports, default, new GraphEvaluationBudget(20_000));

        routing.Failures.Should().BeEmpty();
    }

    [Fact]
    public void A_catalog_of_settlements_reached_by_shared_tables_is_never_left_without_a_picture()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Plaza);
        profile.ComponentNodes.Should().HaveCount(1);

        var watch = Stopwatch.StartNew();
        var outcome = new GraphLayoutSelector().Select(profile, timeLimit: TimeSpan.FromSeconds(5));
        watch.Stop();

        outcome.IsSuccess.Should().BeTrue(Reasons(outcome));
        var candidate = outcome.Best!;
        candidate.Layout.Nodes.Should().HaveCount(profile.NodeCount);
        candidate.Routes.Should().HaveCount(profile.Edges.Count);
        Stacked(candidate.Layout.Nodes).Should().BeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the quick gate must answer quickly even when it cannot finish the search");
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void A_catalog_taken_from_a_report_always_ends_with_a_whole_picture()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog);

        var watch = Stopwatch.StartNew();
        // The product's own ceiling, not one the test picks: the guard is that a catalog of this size gets its
        // routed picture inside the watchdog the app gives it.
        var outcome = new GraphLayoutSelector().Select(profile);
        watch.Stop();

        outcome.Best.Should().NotBeNull("a catalog must always end with a picture, even when the search cannot finish");
        outcome.IsRough.Should().BeFalse("the plain arrangement is the bad picture the reader rejects; a catalog of this size must get its routed one");
        outcome.Best!.Layout.Nodes.Should().HaveCount(profile.NodeCount);
        outcome.Best.Routes.Should().HaveCount(profile.Edges.Count, "every relation of the picture is drawn once");
        Stacked(outcome.Best.Layout.Nodes).Should().BeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(40));
    }

    private static string Reasons(GraphSelectionOutcome outcome) =>
        string.Join(", ", outcome.Reports.SelectMany(
            seed => seed.Failures.Select(failure => $"{seed.Name}#{seed.Component} e{failure.EdgeIndex}:{failure.Kind}")));

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
