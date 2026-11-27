using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks which relations a move may invalidate (refactor 14, §9's local repair).</summary>
public sealed class GraphRerouteSetTests
{
    private const double Height = 40;

    [Fact]
    public void A_move_re_routes_only_the_relations_of_the_card_it_moved()
    {
        var (profile, state, ports, routes) = Pair();
        var warm = Warmup(state, ports, routes);
        var trial = state.Clone();
        trial.Move(0, 40, 0);

        var again = Of(warm, trial, profile);

        again.Should().Equal([0], "only the moved card's own relation can have changed");
    }

    [Fact]
    public void A_wider_card_re_routes_the_relation_that_ends_on_it()
    {
        var (profile, state, ports, routes) = Pair();
        var warm = Warmup(state, ports, routes);
        var trial = state.Clone();
        trial.Widen(2, 240);

        var again = Of(warm, trial, profile);

        again.Should().Equal([1], "the widened card carries the second relation only");
    }

    [Fact]
    public void A_card_moved_onto_another_route_re_routes_that_route_too()
    {
        var cards = new[] { Card(0, 0, 0), Card(1, 200, 0), Card(2, 400, 0), Card(3, 600, 0), Card(4, 800, 0) };
        var edges = new (int, int)[] { (0, 1), (1, 2), (2, 3), (3, 4) };
        var profile = GraphLayoutProfile.Create(cards.Select(card => card.Width).ToArray(), edges);
        var state = GraphPlacementState.From(new GraphDraftLayout("t", cards, 1000, 200), profile);
        var packed = state.Assemble("t");
        var ports = GraphPortAssigner.Assign(packed.Nodes, edges);
        var routing = new GraphNonOrthogonalRouter().Route(packed.Nodes, profile, ports);
        var warm = new GraphRouteWarmup(state, packed.Nodes, ports.Ends, routing.Routes.ToArray());
        var trial = state.Clone();
        trial.Move(4, -700, 0);

        var again = Of(warm, trial, profile);

        again.Should().Contain(3, "the moved card carries the last relation")
            .And.Contain(0, "the moved card now stands across the first relation's route");
    }

    [Fact]
    public void A_relation_the_previous_pass_left_unrouted_is_always_in_the_set()
    {
        var (profile, state, ports, routes) = Pair();
        var warm = new GraphRouteWarmup(state, state.Assemble("t").Nodes, ports.Ends, [routes[0], null]);

        var again = Of(warm, state, profile);

        again.Should().Equal([1]);
    }

    private static GraphRouteWarmup Warmup(GraphPlacementState state, GraphPortAssignment ports, EdgeRoute?[] routes) =>
        new(state, state.Assemble("t").Nodes, ports.Ends, routes);

    private static int[] Of(GraphRouteWarmup warm, GraphPlacementState state, GraphLayoutProfile profile)
    {
        var nodes = state.Assemble("t").Nodes;

        return GraphRerouteSet.Of(warm, nodes, state, GraphPortAssigner.Assign(nodes, profile.Edges), profile);
    }

    /// <summary>Two independent relations far apart, so a move touches exactly one of them.</summary>
    private static (GraphLayoutProfile Profile, GraphPlacementState State, GraphPortAssignment Ports, EdgeRoute?[] Routes) Pair()
    {
        var cards = new[] { Card(0, 0, 0), Card(1, 0, 200), Card(2, 1200, 0), Card(3, 1200, 200) };
        var edges = new (int, int)[] { (0, 1), (2, 3) };
        var profile = GraphLayoutProfile.Create(cards.Select(card => card.Width).ToArray(), edges);
        var state = GraphPlacementState.From(new GraphDraftLayout("t", cards, 1600, 400), profile);
        var ports = GraphPortAssigner.Assign(state.Assemble("t").Nodes, edges);
        var routing = new GraphNonOrthogonalRouter().Route(state.Assemble("t").Nodes, profile, ports);

        return (profile, state, ports, routing.Routes.ToArray());
    }

    private static NodePlacement Card(int index, double x, double y, double width = 100) =>
        new(index, new GraphPoint(x, y), width, Height);
}

