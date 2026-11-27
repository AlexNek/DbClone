using System.Globalization;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks the initial straight and cubic router (refactor 14, milestone E).</summary>
public sealed class GraphNonOrthogonalRouterTests
{
    private const double Height = 40;
    private static readonly GraphNonOrthogonalRouter Router = new();
    private static readonly GraphLayoutValidator Validator = new();

    public static TheoryData<string> Fixtures => new() { "chain", "hub", "cycle", "components", "dense" };

    [Fact]
    public void An_unobstructed_relation_stays_one_straight_path()
    {
        var result = Route([Card(0, 0, 0), Card(1, 400, 0)], [(0, 1)]);

        result.IsComplete.Should().BeTrue();
        result.Routes[0]!.Geometry.Should().Be(EEdgeGeometry.Straight);
        result.Routes[0]!.Points.Should().HaveCount(2);
    }

    [Fact]
    public void A_card_between_two_tables_costs_their_relation_nothing()
    {
        // The card is painted over the ink, so the relation passes behind it: one straight chord, never a bow
        // round the card and never a failure.
        var cards = new[] { Card(0, 0, 0), Card(1, 600, 0), Card(2, 250, 0) };

        var result = Route(cards, [(0, 1)]);

        result.IsComplete.Should().BeTrue();
        var route = result.Routes[0]!;
        route.Geometry.Should().Be(EEdgeGeometry.Straight);
        route.Points.Should().HaveCount(2);
        AssertValid(cards, [(0, 1)], result);
    }

    [Fact]
    public void Three_parallel_relations_are_three_separate_valid_paths()
    {
        var cards = new[] { Card(0, 0, 0, 200), Card(1, 500, 0, 200) };
        var edges = new (int, int)[] { (0, 1), (0, 1), (0, 1) };

        var result = Route(cards, edges);

        result.IsComplete.Should().BeTrue();
        result.Routes.Select(route => string.Join(";", route!.Points.Select(point => $"{point.X:R},{point.Y:R}"))).Distinct().Should().HaveCount(3);
        AssertValid(cards, edges, result);
    }

    [Fact]
    public void Two_constraints_joining_one_pair_state_their_columns_at_different_points()
    {
        // The pair shares one road, so both labels used to stand at its middle and one printed over the
        // other: two constraints read as one with a garbled name. Each states its columns on its own
        // share of its own route instead (D4).
        var cards = new[] { Card(0, 0, 0, 200), Card(1, 500, 0, 200) };
        var edges = new (int, int)[] { (0, 1), (0, 1) };

        var result = Route(cards, edges);

        result.IsComplete.Should().BeTrue();
        var first = result.Routes[0]!;
        var second = result.Routes[1]!;
        var span = Distance(first.Points[0], first.Points[^1]);

        Distance(first.LabelPoint, second.LabelPoint).Should().BeGreaterThan(
            span / 4,
            "two labels printed at one midpoint read as one constraint");
        first.LabelPoint.X.Should().BeInRange(first.Points.Min(point => point.X), first.Points.Max(point => point.X));
        first.LabelPoint.Y.Should().BeInRange(first.Points.Min(point => point.Y), first.Points.Max(point => point.Y));
        AssertValid(cards, edges, result);
    }

    [Fact]
    public void A_self_reference_is_a_valid_loop_clear_of_every_other_stroke()
    {
        var cards = new[] { Card(0, 0, 0, 200), Card(1, 500, 0, 200) };
        var edges = new (int, int)[] { (0, 0), (0, 1), (1, 1) };

        var result = Route(cards, edges);

        result.IsComplete.Should().BeTrue();
        result.Routes[0]!.Shape.Should().Be(EEdgeShape.Loop);
        result.Routes[2]!.Shape.Should().Be(EEdgeShape.Loop);
        AssertValid(cards, edges, result);
    }

    [Fact]
    public void A_relation_with_no_admissible_candidate_is_reported_with_its_blockers_and_never_returned_overlapping()
    {
        // A self-reference is the one relation that still has to dodge: its arcs leave the top border, so a wall
        // standing on that border leaves every one of them inside a card. An ordinary relation would pass behind
        // the wall as a chord, which is why the refusal is pinned on the loop.
        var wall = new NodePlacement(1, new GraphPoint(-500, -1000), 2000, 995);
        var cards = new[] { Card(0, 0, 0), wall };

        var result = Route(cards, [(0, 0)]);

        result.IsComplete.Should().BeFalse();
        result.Routes[0].Should().BeNull();
        var failure = result.Failures.Single();
        failure.EdgeIndex.Should().Be(0);
        failure.Kind.Should().Be(ERouteFailureKind.CardCollision);
        failure.BlockingCards.Should().Equal(1);
        failure.Explored.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_seed_routes_without_a_hard_failure_or_an_orthogonal_dog_leg(string fixture)
    {
        var profile = Profile(fixture);

        foreach (var draft in GraphDraftSeeds.Generate(profile))
        {
            var ports = GraphPortAssigner.Assign(draft.Nodes, profile.Edges);
            var result = Router.Route(draft.Nodes, profile, ports);

            result.Routes.Where(route => route is not null).Should().OnlyContain(
                route => route!.Geometry == EEdgeGeometry.Straight || route.Geometry == EEdgeGeometry.Cubic,
                draft.Name);

            var accepted = result.Routes.Where(route => route is not null).Select(route => route!).ToArray();
            var validity = Validator.Validate(draft.Nodes, accepted, profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());
            validity.IsValid.Should().BeTrue($"{fixture}/{draft.Name}: {string.Join(", ", validity.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}"))}");
        }
    }

    [Theory]
    [InlineData("chain", "ranked")]
    [InlineData("chain", "radial")]
    [InlineData("hub", "radial")]
    [InlineData("components", "ranked")]
    [InlineData("components", "radial")]
    [InlineData("components", "force")]
    [InlineData("cycle", "ranked")]
    [InlineData("cycle", "radial")]
    public void A_seed_that_suits_a_sparse_fixture_routes_it_completely(string fixture, string seed)
    {
        var profile = Profile(fixture);
        var draft = GraphDraftSeeds.Generate(profile).Single(candidate => candidate.Name == seed);

        var result = Router.Route(draft.Nodes, profile, GraphPortAssigner.Assign(draft.Nodes, profile.Edges));

        result.IsComplete.Should().BeTrue(string.Join(", ", result.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
    }
    [Theory]
    [InlineData("chain")]
    [InlineData("cycle")]
    [InlineData("hub")]
    public void Re_routing_every_relation_reproduces_the_full_pass_exactly(string fixture)
    {
        var profile = Profile(fixture);
        var draft = GraphDraftSeeds.Generate(profile).Single(candidate => candidate.Name == "ranked");
        var ports = GraphPortAssigner.Assign(draft.Nodes, profile.Edges);
        var full = Router.Route(draft.Nodes, profile, ports);

        var again = Router.Reroute(draft.Nodes, profile, ports, full.Routes, Enumerable.Range(0, profile.Edges.Count).ToArray());

        Signature(again).Should().Be(Signature(full), "a local pass over everything is the same pass");
    }

    [Fact]
    public void A_re_route_keeps_every_relation_it_was_not_given()
    {
        var profile = Profile("chain");
        var draft = GraphDraftSeeds.Generate(profile).Single(candidate => candidate.Name == "ranked");
        var ports = GraphPortAssigner.Assign(draft.Nodes, profile.Edges);
        var full = Router.Route(draft.Nodes, profile, ports);

        var again = Router.Reroute(draft.Nodes, profile, ports, full.Routes, [3]);

        for (var edge = 0; edge < profile.Edges.Count; edge++)
        {
            if (edge != 3)
            {
                again.Routes[edge].Should().BeSameAs(full.Routes[edge], $"relation {edge} was not re-routed");
            }
        }

        AssertValid(draft.Nodes.ToArray(), profile.Edges.ToArray(), again);
    }

    [Fact]
    public void A_kept_route_travels_with_the_component_it_was_routed_for()
    {
        var profile = Profile("chain");
        var draft = GraphDraftSeeds.Generate(profile).Single(candidate => candidate.Name == "ranked");
        var ports = GraphPortAssigner.Assign(draft.Nodes, profile.Edges);
        var full = Router.Route(draft.Nodes, profile, ports);

        // Packing translates a whole component, so every port of this one moves by the same offset.
        var moved = draft.Nodes
            .Select(card => new NodePlacement(card.NodeIndex, new GraphPoint(card.TopLeft.X + 40, card.TopLeft.Y + 25), card.Width, card.Height))
            .ToArray();
        var movedPorts = GraphPortAssigner.Assign(moved, profile.Edges);

        var again = Router.Reroute(moved, profile, movedPorts, full.Routes, [3]);

        AssertOnTheirPorts(movedPorts, profile.Edges.ToArray(), again);
        for (var edge = 0; edge < profile.Edges.Count; edge++)
        {
            if (edge != 3)
            {
                again.Routes[edge].Should().NotBeSameAs(full.Routes[edge], "the route travelled with the component");
            }
        }
    }

    [Fact]
    public void A_kept_route_whose_other_end_no_longer_reaches_its_port_is_routed_again()
    {
        var profile = Profile("chain");
        var draft = GraphDraftSeeds.Generate(profile).Single(candidate => candidate.Name == "ranked");
        var ports = GraphPortAssigner.Assign(draft.Nodes, profile.Edges);
        var full = Router.Route(draft.Nodes, profile, ports);

        // One card changed shape, so the routes that end on it cannot simply travel: a shift that lands one end
        // on its port leaves the other end off its own.
        var moved = draft.Nodes.ToArray();
        moved[5] = moved[5] with { TopLeft = new GraphPoint(moved[5].TopLeft.X + 12, moved[5].TopLeft.Y) };
        var movedPorts = GraphPortAssigner.Assign(moved, profile.Edges);

        var again = Router.Reroute(moved, profile, movedPorts, full.Routes, []);

        again.IsComplete.Should().BeTrue(string.Join(", ", again.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
        again.Routes[4].Should().NotBeSameAs(full.Routes[4], "its child card moved");
        again.Routes[5].Should().NotBeSameAs(full.Routes[5], "its parent card moved");
        AssertOnTheirPorts(movedPorts, profile.Edges.ToArray(), again);
    }

    [Fact]
    public void A_relation_the_previous_pass_could_not_route_is_routed_again_even_when_not_named()
    {
        var wall = new NodePlacement(1, new GraphPoint(-500, -1000), 2000, 995);
        var cards = new[] { Card(0, 0, 0), wall };
        var edges = new (int, int)[] { (0, 0) };
        var profile = GraphLayoutProfile.Create(cards.Select(card => card.Width).ToArray(), edges);
        var ports = GraphPortAssigner.Assign(cards, edges);
        var first = Router.Route(cards, profile, ports);
        first.IsComplete.Should().BeFalse("the wall leaves no candidate");

        var again = Router.Reroute(cards, profile, ports, first.Routes, []);

        again.IsComplete.Should().BeFalse();
        again.Failures.Should().ContainSingle().Which.Kind.Should().Be(ERouteFailureKind.CardCollision);
    }

    [Fact]
    public void A_re_route_that_names_no_relation_keeps_the_layout_as_it_was()
    {
        var profile = Profile("hub");
        var draft = GraphDraftSeeds.Generate(profile).Single(candidate => candidate.Name == "ranked");
        var ports = GraphPortAssigner.Assign(draft.Nodes, profile.Edges);
        var full = Router.Route(draft.Nodes, profile, ports);

        var again = Router.Reroute(draft.Nodes, profile, ports, full.Routes, []);

        Signature(again).Should().Be(Signature(full));
        again.Crossings.Should().BeEmpty("a kept route was already checked when it was chosen");
    }

    [Fact]
    public void The_same_placement_gives_the_same_routes_under_any_culture()
    {
        var profile = Profile("dense");
        var draft = GraphRankedSeed.Place(profile);
        var ports = GraphPortAssigner.Assign(draft.Nodes, profile.Edges);

        var first = Signature(Router.Route(draft.Nodes, profile, ports));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Signature(Router.Route(draft.Nodes, profile, ports)).Should().Be(first);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_crossings_the_router_reports_match_the_ones_the_validator_finds()
    {
        var profile = Profile("dense");
        var draft = GraphRankedSeed.Place(profile);
        var result = Router.Route(draft.Nodes, profile, GraphPortAssigner.Assign(draft.Nodes, profile.Edges));
        var accepted = result.Routes.Where(route => route is not null).Select(route => route!).ToArray();

        var validity = Validator.Validate(draft.Nodes, accepted, profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());

        result.Crossings.Select(crossing => (crossing.FirstEdge, crossing.SecondEdge))
            .Should().BeEquivalentTo(validity.Crossings.Select(crossing => (crossing.FirstEdge, crossing.SecondEdge)));
    }

    [Fact]
    public void A_cancelled_request_throws_and_returns_no_partial_routes()
    {
        var profile = Profile("chain");
        var draft = GraphRankedSeed.Place(profile);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var act = () => Router.Route(draft.Nodes, profile, GraphPortAssigner.Assign(draft.Nodes, profile.Edges), source.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void An_incomplete_port_assignment_is_returned_as_its_capacity_failures_without_routes()
    {
        var edges = Enumerable.Range(1, 14).Select(node => (node, 0)).ToArray();
        var cards = new[] { Card(0, 0, 0, 72) }
            .Concat(Enumerable.Range(1, 14).Select(node => Card(node, (node % 7 * 150) - 500, node < 8 ? -300 : 300, 72)))
            .ToArray();
        var profile = GraphLayoutProfile.Create(cards.Select(card => card.Width).ToArray(), edges);

        var result = Router.Route(cards, profile, GraphPortAssigner.Assign(cards, edges));

        result.IsComplete.Should().BeFalse();
        result.Failures.Should().OnlyContain(failure => failure.Kind == ERouteFailureKind.PortCapacity);
        result.Routes.Should().OnlyContain(route => route == null);
    }

    private static GraphRoutingResult Route(NodePlacement[] cards, (int Child, int Parent)[] edges)
    {
        var profile = GraphLayoutProfile.Create(cards.Select(card => card.Width).ToArray(), edges);

        return Router.Route(cards, profile, GraphPortAssigner.Assign(cards, edges));
    }

    private static void AssertValid(NodePlacement[] cards, (int Child, int Parent)[] edges, GraphRoutingResult result)
    {
        var validity = Validator.Validate(
            cards,
            result.Routes.Select(route => route!).ToArray(),
            edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());

        validity.IsValid.Should().BeTrue(string.Join(", ", validity.Failures.Select(failure => $"{failure.EdgeIndex}:{failure.Kind}")));
    }

    /// <summary>Every route starts on its child port and ends on its parent port, wherever the cards have moved to.</summary>
    private static void AssertOnTheirPorts(GraphPortAssignment ports, (int Child, int Parent)[] edges, GraphRoutingResult result)
    {
        for (var edge = 0; edge < edges.Length; edge++)
        {
            var ends = ports.Ends[edge]!;
            var route = result.Routes[edge]!;
            Distance(route.Points[0], ends.Child.Point).Should().BeLessThan(0.5, $"relation {edge} must leave its child port");
            Distance(route.Points[^1], ends.Parent.Point).Should().BeLessThan(0.5, $"relation {edge} must arrive on its parent port");
        }
    }

    private static double Distance(GraphPoint first, GraphPoint second) =>
        Math.Sqrt(((first.X - second.X) * (first.X - second.X)) + ((first.Y - second.Y) * (first.Y - second.Y)));

    private static NodePlacement Card(int index, double x, double y, double width = 100) =>
        new(index, new GraphPoint(x, y), width, Height);

    private static string Signature(GraphRoutingResult result) =>
        string.Join(
            "|",
            result.Routes.Select(route => route is null
                ? "-"
                : route.Geometry + ":" + string.Join(",", route.Points.Select(point => point.X.ToString("R", CultureInfo.InvariantCulture) + " " + point.Y.ToString("R", CultureInfo.InvariantCulture)))));

    private static GraphLayoutProfile Profile(string fixture)
    {
        var edges = Edges(fixture);
        var count = fixture switch { "chain" => 12, "hub" => 15, "cycle" => 8, "components" => 9, _ => 20 };
        var degree = new int[count];
        foreach (var (child, parent) in edges)
        {
            degree[child]++;
            degree[parent]++;
        }

        var widths = Enumerable.Range(0, count)
            .Select(node => Math.Max(72.0 + ((node * 13) % 90), GraphPortAssigner.MinimumWidth(degree[node], Height)))
            .ToArray();

        return GraphLayoutProfile.Create(widths, edges);
    }

    private static (int Child, int Parent)[] Edges(string fixture) => fixture switch
    {
        "chain" => Enumerable.Range(1, 11).Select(node => (node, node - 1)).ToArray(),
        "hub" => Enumerable.Range(1, 14).Select(node => (node, 0)).ToArray(),
        "cycle" => [(0, 1), (1, 2), (2, 3), (3, 0), (4, 0), (5, 4), (6, 5), (7, 7), (7, 6), (2, 6)],
        "components" => [(1, 0), (2, 0), (4, 3), (5, 3), (7, 6), (8, 6), (8, 7)],
        _ => Enumerable.Range(0, 20).SelectMany(node => new[] { (node, (node + 3) % 20), (node, (node + 7) % 20) }).ToArray(),
    };
}
