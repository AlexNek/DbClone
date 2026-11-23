using System.Globalization;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks the lexicographic score, crossing removal and candidate selection (refactor 14, milestone G).</summary>
public sealed class GraphLayoutSelectionTests
{
    private const double Height = 40;
    private static readonly GraphLayoutValidator Validator = new();

    [Fact]
    public void A_crossing_free_layout_beats_a_shorter_one_with_a_crossing()
    {
        var clean = Score(crossings: 0, length: 5000);
        var crossed = Score(crossings: 1, length: 10);

        clean.CompareTo(crossed).Should().BeLessThan(0);
    }

    [Fact]
    public void A_layout_with_no_relation_drawn_up_the_page_beats_a_shorter_one_with_one()
    {
        var ordered = Score(rank: 0, length: 5000);
        var upward = Score(rank: 1, length: 10);

        ordered.CompareTo(upward).Should().BeLessThan(0);
    }

    [Fact]
    public void Crossings_are_compared_before_rank_violations()
    {
        var spread = Score(crossings: 0, rank: 9);
        var folded = Score(crossings: 1, rank: 0);
        spread.CompareTo(folded).Should().BeLessThan(0, "one relation drawn up the page never outweighs a crossing");
    }

    [Fact]
    public void A_relation_drawn_over_a_foreign_card_loses_to_one_in_clear_ground_at_equal_rank()
    {
        var clear = Score(rank: 2, blocked: 0);
        var over = Score(rank: 2, blocked: 3);
        clear.CompareTo(over).Should().BeLessThan(0, "a reader cannot tell which table a line running through another belongs to");
    }

    [Fact]
    public void Clear_ground_never_buys_a_relation_drawn_up_the_page()
    {
        var upward = Score(rank: 1, blocked: 40);
        var ordered = Score(rank: 0, blocked: 41);
        ordered.CompareTo(upward).Should().BeLessThan(0, "the picture still climbs down the page first");
    }

    [Fact]
    public void Length_decides_only_between_equal_crossings_and_rank_and_the_seed_settles_the_rest()
    {
        Score(length: 100).CompareTo(Score(length: 200)).Should().BeLessThan(0);
        Score(seed: 0).CompareTo(Score(seed: 1)).Should().BeLessThan(0);
        Score(seed: 3).CompareTo(Score(seed: 3)).Should().Be(0);
    }

    [Fact]
    public void The_score_counts_a_child_standing_above_its_parent_and_ignores_a_cycle()
    {
        var profile = GraphLayoutProfile.Create([100, 100, 100, 100], [(0, 1), (2, 3), (3, 2)]);
        var nodes = new NodePlacement[]
        {
            new(0, new GraphPoint(0, 0), 100, Height),
            new(1, new GraphPoint(0, 200), 100, Height),
            new(2, new GraphPoint(300, 0), 100, Height),
            new(3, new GraphPoint(300, 200), 100, Height),
        };
        var layout = new GraphDraftLayout("t", nodes, 600, 400);
        var candidate = new GraphLayoutCandidate("t", layout, [], [], [], 0, 0, 0);

        GraphLayoutScore.Of(candidate, profile, 0).RankViolations.Should().Be(1, "only the plain relation counts; a cycle must climb somewhere");
    }

    [Fact]
    public void Route_substitution_removes_a_crossing_when_an_alternative_route_exists()
    {
        var nodes = new NodePlacement[]
        {
            new(0, new GraphPoint(0, 0), 100, Height),
            new(1, new GraphPoint(400, 0), 100, Height),
            new(2, new GraphPoint(0, 200), 100, Height),
            new(3, new GraphPoint(400, 200), 100, Height),
        };
        var edges = new (int Child, int Parent)[] { (0, 1), (2, 3) };
        var profile = GraphLayoutProfile.Create(nodes.Select(node => node.Width).ToArray(), edges);
        var ends = GraphPortAssigner.Assign(nodes, edges).Ends.Select(item => item!).ToArray();
        var routes = new[]
        {
            Cubic(0, ends[0], 200),
            Cubic(1, ends[1], -200),
        };
        var endpoints = edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray();
        var before = Validator.Validate(nodes, routes, endpoints);
        before.IsValid.Should().BeTrue("the starting layout is valid and only crossed");
        before.Crossings.Should().NotBeEmpty();
        var candidate = new GraphLayoutCandidate("t", new GraphDraftLayout("t", nodes, 600, 400), ends, routes, before.Crossings, 0, 0, 0);

        var reduced = new GraphCrossingRemoval().Substitute(candidate, profile, new GraphEvaluationBudget(10_000));

        reduced.CrossingCount.Should().Be(0);
        Validator.Validate(nodes, reduced.Routes, endpoints).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_chain_selects_a_crossing_free_layout_that_reads_top_down()
    {
        var profile = Chain(8);

        var outcome = new GraphLayoutSelector().Select(profile);

        outcome.IsSuccess.Should().BeTrue();
        var best = outcome.Best!;
        var score = GraphLayoutScore.Of(best, profile, 0);
        score.Crossings.Should().Be(0);
        score.RankViolations.Should().Be(0);
        outcome.Reports.Should().HaveCount(5);
        AssertValid(best, profile);
    }

    [Fact]
    public void The_chosen_layout_is_the_best_score_among_the_valid_seeds()
    {
        var profile = Hub(6);

        var outcome = new GraphLayoutSelector().Select(profile);

        outcome.IsSuccess.Should().BeTrue();
        var valid = outcome.Reports.Where(report => report.IsValid).Select(report => report.Score!.Value).ToArray();
        valid.Should().NotBeEmpty();
        var minimum = valid.Min();
        GraphLayoutScore.Of(outcome.Best!, profile, minimum.SeedOrdinal).CompareTo(minimum).Should().Be(0);
        AssertValid(outcome.Best!, profile);
    }

    [Fact]
    public void Selection_is_deterministic_under_a_non_invariant_culture()
    {
        var profile = Hub(6);

        var first = Signature(new GraphLayoutSelector().Select(profile));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Signature(new GraphLayoutSelector().Select(profile)).Should().Be(first);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void An_exhausted_budget_still_yields_a_complete_picture_flagged_as_the_plain_arrangement()
    {
        var profile = Hub(6);

        var outcome = new GraphLayoutSelector().Select(profile, new GraphEvaluationBudget(3));

        outcome.Reports.Should().NotBeEmpty();
        outcome.Reports.Should().OnlyContain(report => !report.IsValid && report.ExhaustedBudgets.Count > 0 && report.Failures.Count > 0);
        outcome.IsRough.Should().BeTrue("no search reached a proven arrangement, so the plain one stands in for it");
        outcome.Best!.Layout.Nodes.Should().HaveCount(profile.NodeCount, "a picture the reader can judge beats a window that shows nothing");
        outcome.Best.Routes.Should().HaveCount(profile.Edges.Count);
        outcome.Best.Routes.Should().OnlyContain(route => route.Geometry == EEdgeGeometry.Straight && route.Points.Count == 2);
    }

    [Fact]
    public void A_cancelled_request_returns_no_layout_even_if_a_seed_had_finished()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var outcome = new GraphLayoutSelector().Select(Hub(6), null, source.Token);

        outcome.WasCancelled.Should().BeTrue();
        outcome.Best.Should().BeNull();
    }

    [Fact]
    public void A_watchdog_that_fires_returns_a_complete_layout_and_says_it_is_degraded()
    {
        var profile = Hub(6);

        var outcome = new GraphLayoutSelector().Select(profile, null, default, TimeSpan.Zero);

        outcome.WasCancelled.Should().BeFalse("the caller's token never fired; the time limit ended the search");
        outcome.WasTimedOut.Should().BeTrue();
        outcome.IsDegraded.Should().BeTrue();
        outcome.IsSuccess.Should().BeTrue("a worse picture with a warning beats no picture at all");
        outcome.Best!.Layout.Nodes.Should().HaveCount(profile.NodeCount, "every table is drawn, none is dropped");
        outcome.Best.Routes.Should().HaveCount(profile.Edges.Count, "every relation is routed, never shared or left out");
        AssertValid(outcome.Best, profile);
    }

    [Fact]
    public void A_degraded_request_still_draws_every_component_it_did_not_reach()
    {
        var widths = Enumerable.Range(0, 10)
            .Select(node => Math.Max(72.0, GraphPortAssigner.MinimumWidth(node is 0 or 5 ? 4 : 1, Height)))
            .ToArray();
        var edges = new (int Child, int Parent)[] { (1, 0), (2, 0), (3, 0), (4, 0), (6, 5), (7, 5), (8, 5), (9, 5) };
        var profile = GraphLayoutProfile.Create(widths, edges);
        profile.ComponentNodes.Should().HaveCount(2, "the fixture must use the fallback for more than one component");

        var outcome = new GraphLayoutSelector().Select(profile, null, default, TimeSpan.Zero);

        outcome.IsDegraded.Should().BeTrue();
        outcome.Best!.Layout.Nodes.Should().HaveCount(10, "the component the search had not reached is laid out too");
        outcome.Best.Routes.Should().HaveCount(8);
        outcome.IsRough.Should().BeFalse("the cheap pass still routes and repairs; the plain arrangement is the last resort");
        AssertValid(outcome.Best, profile);
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void The_first_picture_of_the_largest_fixture_is_complete_and_proven()
    {
        // The reader's report: a catalog of this size reached no picture at all, because the pass that
        // answers once the watchdog has fired spent one evaluation and no repair. It must now reach a
        // proven complete arrangement — the plain arrangement is the last resort, not the first answer.
        var profile = GraphQualityFixtures.Profile("scale");

        var outcome = new GraphLayoutSelector().Select(profile, timeLimit: TimeSpan.Zero);

        outcome.WasTimedOut.Should().BeTrue("no refining is allowed once the watchdog has fired");
        outcome.IsSuccess.Should().BeTrue();
        outcome.IsRough.Should().BeFalse("the cheap pass repairs the seed until every relation is routed");
        outcome.Best!.Layout.Nodes.Should().HaveCount(profile.NodeCount, "every table is drawn");
        outcome.Best.Routes.Should().HaveCount(profile.Edges.Count, "every relation is routed");
        AssertValid(outcome.Best, profile);
    }

    [Fact]
    public void The_plain_arrangement_draws_every_card_and_every_relation_between_two_border_points()
    {
        var profile = Hub(6);

        var rough = GraphRoughLayout.Of(profile);

        rough.Layout.Nodes.Should().HaveCount(profile.NodeCount, "no table is dropped");
        rough.Routes.Should().HaveCount(profile.Edges.Count, "no relation loses its arrow");
        rough.Routes.Select(route => route.EdgeIndex).Should().Equal(Enumerable.Range(0, profile.Edges.Count));
        rough.Routes.Should().OnlyContain(route => route.Geometry == EEdgeGeometry.Straight && route.Points.Count == 2);
        rough.Routes.SelectMany(route => route.Points).Should().OnlyContain(point => double.IsFinite(point.X) && double.IsFinite(point.Y));
    }

    [Fact]
    public void A_plain_arrangement_is_drawn_for_a_catalog_of_several_parts_without_losing_one()
    {
        // The reader's crash: a component no seed could finish made the last-resort picture ask one part's
        // seed for every part's card, and the picture died on a card that part did not hold.
        var widths = Enumerable.Range(0, 10)
            .Select(node => Math.Max(72.0, GraphPortAssigner.MinimumWidth(node is 0 or 5 ? 4 : 1, Height)))
            .ToArray();
        var edges = new (int Child, int Parent)[] { (1, 0), (2, 0), (3, 0), (4, 0), (6, 5), (7, 5), (8, 5), (9, 5) };
        var profile = GraphLayoutProfile.Create(widths, edges);
        profile.ComponentNodes.Should().HaveCount(2, "the fixture must fail on a catalog of more than one part");

        var outcome = new GraphLayoutSelector().Select(profile, new GraphEvaluationBudget(3));

        outcome.IsRough.Should().BeTrue("no search reached a proven arrangement");
        outcome.Best!.Layout.Nodes.Should().HaveCount(profile.NodeCount, "every table of every part is drawn");
        outcome.Best.Routes.Should().HaveCount(profile.Edges.Count, "every relation of every part is drawn");
        AssertValid(outcome.Best, profile);
    }

    [Fact]
    public void The_plain_arrangement_keeps_the_shape_of_the_seed_that_came_nearest()
    {
        // The reader's rule: the table many tables name stands in the middle of them. A catalog no arrangement
        // can prove still has to be shown in the arrangement that suited it most, not redrawn in the lattice.
        var profile = GraphQualityFixtures.Profile("hub");
        var radial = GraphRadialSeed.Place(profile);
        var hub = Enumerable.Range(0, profile.NodeCount).MaxBy(node => profile.Neighbours[node].Count);
        var ring = profile.Neighbours[hub];

        var rough = GraphRoughLayout.Of(profile, radial);
        var ranked = GraphRoughLayout.Of(profile);

        rough.Name.Should().Be("rough-radial", "the picture says which arrangement stood in");
        rough.Layout.Nodes.Should().HaveCount(profile.NodeCount, "no table is dropped");
        rough.Routes.Should().HaveCount(profile.Edges.Count, "no relation loses its arrow");
        Distance(rough).Should().BeLessThan(Distance(ranked),
            "the ring is drawn around the table it names, where the lattice pins it to a corner of the rings");

        double Distance(GraphLayoutCandidate candidate)
        {
            var centre = new GraphPoint(
                ring.Average(node => candidate.Layout.Nodes[node].Centre.X),
                ring.Average(node => candidate.Layout.Nodes[node].Centre.Y));

            var card = candidate.Layout.Nodes[hub].Centre;

            return Math.Sqrt(Math.Pow(card.X - centre.X, 2) + Math.Pow(card.Y - centre.Y, 2));
        }
    }

    [Fact]
    public void A_cancelled_request_never_falls_back_to_a_degraded_layout()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var outcome = new GraphLayoutSelector().Select(Hub(6), null, source.Token, TimeSpan.FromMinutes(5));

        outcome.WasCancelled.Should().BeTrue();
        outcome.IsDegraded.Should().BeFalse();
        outcome.Best.Should().BeNull("the user cancelled: nothing is drawn at all");
    }

    [Fact]
    public void A_watchdog_that_does_not_fire_leaves_the_chosen_layout_unchanged()
    {
        var profile = Hub(6);

        var limited = new GraphLayoutSelector().Select(profile, null, default, TimeSpan.FromMinutes(5));
        var open = new GraphLayoutSelector().Select(profile);

        limited.WasTimedOut.Should().BeFalse();
        limited.IsSuccess.Should().BeTrue();
        Signature(limited).Should().Be(Signature(open), "the watchdog must not choose a different candidate");
    }

    private static GraphLayoutScore Score(int crossings = 0, int rank = 0, int blocked = 0, double overlap = 0, double length = 100, int seed = 0) =>
        new(crossings, rank, blocked, overlap, length, length, 0, 1000, 5000, 100, seed);

    private static EdgeRoute Cubic(int edge, GraphEdgeEnds ends, double lift) =>
        new(
            edge,
            EEdgeShape.Line,
            EEdgeGeometry.Cubic,
            [
                ends.Child.Point,
                new GraphPoint(ends.Child.Point.X + 50, ends.Child.Point.Y + lift),
                new GraphPoint(ends.Parent.Point.X - 50, ends.Parent.Point.Y + lift),
                ends.Parent.Point,
            ],
            ends.Child.Point);

    private static void AssertValid(GraphLayoutCandidate candidate, GraphLayoutProfile profile)
    {
        var validity = Validator.Validate(
            candidate.Layout.Nodes,
            candidate.Routes,
            profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());

        validity.IsValid.Should().BeTrue(string.Join(", ", validity.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
        candidate.Routes.Should().OnlyContain(route => route.Geometry == EEdgeGeometry.Straight || route.Geometry == EEdgeGeometry.Cubic);
    }

    private static string Signature(GraphSelectionOutcome outcome) =>
        outcome.Best is null
            ? "none"
            : outcome.Best.Name + "|" + string.Join(
                "|",
                outcome.Best.Layout.Nodes.Select(node => node.TopLeft.X.ToString("R", CultureInfo.InvariantCulture) + " " + node.TopLeft.Y.ToString("R", CultureInfo.InvariantCulture)));

    private static GraphLayoutProfile Hub(int satellites)
    {
        var edges = Enumerable.Range(1, satellites).Select(node => (node, 0)).ToArray();
        var widths = Enumerable.Range(0, satellites + 1)
            .Select(node => Math.Max(72.0 + ((node * 13) % 90), GraphPortAssigner.MinimumWidth(node == 0 ? satellites : 1, Height)))
            .ToArray();

        return GraphLayoutProfile.Create(widths, edges);
    }

    private static GraphLayoutProfile Chain(int count)
    {
        var edges = Enumerable.Range(1, count - 1).Select(node => (node, node - 1)).ToArray();
        var widths = Enumerable.Range(0, count).Select(node => Math.Max(72.0 + ((node * 13) % 90), GraphPortAssigner.MinimumWidth(2, Height))).ToArray();

        return GraphLayoutProfile.Create(widths, edges);
    }
}
