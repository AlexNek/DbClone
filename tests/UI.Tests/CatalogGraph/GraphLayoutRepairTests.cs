using System.Globalization;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks conflict-set rip-up, placement repair and the budgets (refactor 14, milestone F).</summary>
public sealed class GraphLayoutRepairTests
{
    private const double Height = 40;
    private static readonly GraphLayoutValidator Validator = new();

    [Fact]
    public void A_rip_up_pass_never_adds_a_failure_and_never_commits_an_invalid_route()
    {
        var profile = Hub(14, sized: true);
        var draft = GraphRankedSeed.Place(profile);
        var ports = GraphPortAssigner.Assign(draft.Nodes, profile.Edges);
        var first = new GraphNonOrthogonalRouter().Route(draft.Nodes, profile, ports);

        var budget = new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations);
        var resolved = new GraphConflictResolver().Resolve(draft.Nodes, profile, ports, first, budget);

        resolved.Failures.Count.Should().BeLessThanOrEqualTo(first.Failures.Count);
        AssertValid(draft.Nodes, profile, resolved.Routes);
    }

    [Fact]
    public void A_ranked_hub_that_does_not_route_first_time_ends_as_a_complete_valid_layout()
    {
        var profile = Hub(14, sized: true);
        var draft = GraphRankedSeed.Place(profile);

        var outcome = new GraphLayoutRepair().Repair(draft, profile, NewBudget());

        outcome.IsSuccess.Should().BeTrue(string.Join(", ", outcome.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
        var candidate = outcome.Candidate!;
        candidate.Routes.Should().HaveCount(14).And.OnlyContain(route => route.Geometry == EEdgeGeometry.Straight || route.Geometry == EEdgeGeometry.Cubic);
        AssertValid(candidate.Layout.Nodes, profile, candidate.Routes);
        candidate.Ends.Select(ends => ends.Parent.Point).Distinct().Should().HaveCount(14, "no coincident endpoint is placed");
    }

    [Fact]
    public void A_force_seeded_chain_is_repaired_into_a_complete_valid_layout()
    {
        var profile = Chain(12);
        var draft = GraphForceSeed.Place(profile, GraphForceVariant.Wide);

        var outcome = new GraphLayoutRepair().Repair(draft, profile, NewBudget());

        outcome.IsSuccess.Should().BeTrue(string.Join(", ", outcome.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
        AssertValid(outcome.Candidate!.Layout.Nodes, profile, outcome.Candidate.Routes);
    }

    [Fact]
    public void A_port_capacity_failure_is_resolved_by_widening_the_card_and_never_by_a_coincident_port()
    {
        var profile = Hub(14, sized: false);
        var draft = GraphRadialSeed.Place(profile);
        GraphPortAssigner.Assign(draft.Nodes, profile.Edges).IsComplete.Should().BeFalse("the cards start too narrow for the hub's ends");

        var outcome = new GraphLayoutRepair().Repair(draft, profile, NewBudget());

        outcome.IsSuccess.Should().BeTrue(string.Join(", ", outcome.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
        var candidate = outcome.Candidate!;
        candidate.Layout.Nodes[0].Width.Should().BeGreaterThanOrEqualTo(GraphPortAssigner.MinimumWidth(14, Height));
        candidate.Layout.Nodes[0].Width.Should().BeGreaterThan(draft.Nodes[0].Width);
        candidate.Ends.Select(ends => ends.Parent.Point).Distinct().Should().HaveCount(14);
        AssertValid(candidate.Layout.Nodes, profile, candidate.Routes);
    }

    [Fact]
    public void An_exhausted_evaluation_budget_is_an_explicit_failure_and_never_a_partial_layout()
    {
        var profile = Hub(14, sized: true);
        var draft = GraphRankedSeed.Place(profile);

        var outcome = new GraphLayoutRepair().Repair(draft, profile, new GraphEvaluationBudget(5));

        outcome.IsSuccess.Should().BeFalse();
        outcome.Candidate.Should().BeNull();
        outcome.ExhaustedBudgets.Should().Contain(nameof(GraphSearchBudget.MaximumGeometryEvaluations));
        outcome.Failures.Should().NotBeEmpty();
        outcome.Failures.Should().OnlyContain(failure => failure.EdgeIndex >= 0 && failure.EdgeIndex < 14);
    }

    [Fact]
    public void An_exhausted_round_budget_names_the_budget_that_ended_the_search()
    {
        // The cards start too narrow for the hub's ends, so the first evaluation is incomplete and only a
        // repair round can change it: with no round left the search ends by naming that budget.
        var profile = Hub(14, sized: false);
        var draft = GraphRadialSeed.Place(profile);
        GraphPortAssigner.Assign(draft.Nodes, profile.Edges).IsComplete.Should().BeFalse();

        var outcome = new GraphLayoutRepair(maximumRounds: 0).Repair(draft, profile, NewBudget());

        outcome.IsSuccess.Should().BeFalse();
        outcome.ExhaustedBudgets.Should().Contain(nameof(GraphSearchBudget.MaximumRepairRounds));
    }

    [Fact]
    public void A_cancelled_repair_reports_cancellation_and_returns_no_layout()
    {
        var profile = Hub(14, sized: true);
        var draft = GraphRankedSeed.Place(profile);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var outcome = new GraphLayoutRepair().Repair(draft, profile, NewBudget(), source.Token);

        outcome.WasCancelled.Should().BeTrue();
        outcome.Candidate.Should().BeNull();
    }

    [Fact]
    public void The_same_seed_is_repaired_to_the_same_coordinates_under_any_culture()
    {
        var profile = Hub(14, sized: true);
        var draft = GraphForceSeed.Place(profile, GraphForceVariant.Standard);

        var first = Signature(new GraphLayoutRepair().Repair(draft, profile, NewBudget()));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Signature(new GraphLayoutRepair().Repair(draft, profile, NewBudget())).Should().Be(first);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_conflict_set_is_capped_and_the_budgets_are_named()
    {
        GraphSearchBudget.MaximumConflictSetSize.Should().BeGreaterThan(1);
        GraphSearchBudget.MaximumConflictAssignments.Should().BeGreaterThan(0);
        GraphSearchBudget.MaximumRipUpRounds.Should().BeGreaterThan(0);
        GraphSearchBudget.MaximumRepairMovesPerComponent.Should().BeGreaterThan(0);
        GraphSearchBudget.MaximumExpansions.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_crossing_removal_move_never_costs_a_hard_rule_and_never_adds_a_crossing()
    {
        // Two relations on parallel rows are bent across each other. The placement half of the crossing
        // search may move a card or give up, but whatever it returns is still complete and no worse.
        var nodes = new NodePlacement[]
        {
            new(0, new GraphPoint(0, 0), 100, Height),
            new(1, new GraphPoint(400, 0), 100, Height),
            new(2, new GraphPoint(0, 200), 100, Height),
            new(3, new GraphPoint(400, 200), 100, Height),
        };
        var profile = GraphLayoutProfile.Create([100, 100, 100, 100], [(0, 1), (2, 3)]);
        var ends = GraphPortAssigner.Assign(nodes, profile.Edges).Ends.Select(item => item!).ToArray();
        var routes = new[] { Bent(0, ends[0], 200), Bent(1, ends[1], -200) };
        var endpoints = profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray();
        var validity = Validator.Validate(nodes, routes, endpoints);
        validity.IsValid.Should().BeTrue("the starting layout is valid and only crossed");
        validity.Crossings.Should().NotBeEmpty();

        var candidate = new GraphLayoutCandidate("crossed", new GraphDraftLayout("crossed", nodes, 600, 400), ends, routes, validity.Crossings, 0, 0, 0);

        var reduced = new GraphLayoutRepair().ReduceCrossingsByMoving(candidate, profile, NewBudget());

        reduced.CrossingCount.Should().BeLessThanOrEqualTo(candidate.CrossingCount);
        AssertValid(reduced.Layout.Nodes, profile, reduced.Routes);

        if (reduced.CrossingCount == candidate.CrossingCount)
        {
            // A move is kept only when it strictly reduces the count, so a round that found nothing must
            // hand back the placement it was given rather than a shuffled copy of it.
            reduced.Layout.Nodes.Select(node => node.TopLeft).Should().Equal(candidate.Layout.Nodes.Select(node => node.TopLeft));
        }
    }

    private static GraphEvaluationBudget NewBudget() => new(GraphSearchBudget.MaximumGeometryEvaluations);

    private static EdgeRoute Bent(int edge, GraphEdgeEnds ends, double lift) =>
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

    private static void AssertValid(IReadOnlyList<NodePlacement> nodes, GraphLayoutProfile profile, IReadOnlyList<EdgeRoute?> routes)
    {
        var validity = Validator.Validate(
            nodes,
            routes.Where(route => route is not null).Select(route => route!).ToArray(),
            profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());

        validity.IsValid.Should().BeTrue(string.Join(", ", validity.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
    }

    private static string Signature(GraphRepairOutcome outcome) =>
        outcome.Candidate is null
            ? "failed"
            : string.Join(
                "|",
                outcome.Candidate.Layout.Nodes.Select(node => node.TopLeft.X.ToString("R", CultureInfo.InvariantCulture) + " " + node.TopLeft.Y.ToString("R", CultureInfo.InvariantCulture))
                    .Concat(outcome.Candidate.Routes.Select(route => route.Geometry + string.Join(",", route.Points.Select(point => point.X.ToString("R", CultureInfo.InvariantCulture) + " " + point.Y.ToString("R", CultureInfo.InvariantCulture))))));

    private static GraphLayoutProfile Hub(int satellites, bool sized)
    {
        var edges = Enumerable.Range(1, satellites).Select(node => (node, 0)).ToArray();
        var widths = Enumerable.Range(0, satellites + 1)
            .Select(node => sized ? Math.Max(72.0 + ((node * 13) % 90), GraphPortAssigner.MinimumWidth(node == 0 ? satellites : 1, Height)) : 72.0)
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
