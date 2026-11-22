namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Routes every relation of one placement as a straight segment or a smooth cubic. Relations are routed
/// most-constrained first; each candidate is checked against the cards and the routes already reserved, and
/// a candidate with a hard failure is never committed. A searching pass prefers a crossing-free candidate
/// over any with crossings and otherwise keeps the first with the fewest; a pass that must place every
/// relation first — the one the watchdog leaves — takes the first admissible candidate and leaves the
/// crossings to the removal that follows. A relation that finds no admissible candidate is reported as a
/// <see cref="RouteFailure"/> naming the blockers: the router never returns the best invalid route, and it
/// never moves a card or reroutes a committed relation (that is the repair's job).
/// </summary>
internal sealed class GraphNonOrthogonalRouter
{
    /// <summary>How far a kept route's far end may sit from its parent port and still count as carried with it.</summary>
    private const double KeptEndpointTolerance = 0.5;

    private readonly GraphLayoutValidator _validator = new();

    /// <summary>Routes the placement.</summary>
    /// <param name="nodes">The cards, by node index.</param>
    /// <param name="profile">The graph facts and the relations.</param>
    /// <param name="ports">The border ports of every relation end.</param>
    /// <param name="cancellation">Checked between relations.</param>
    /// <param name="budget">The shared evaluation budget; a fresh one when omitted.</param>
    /// <param name="hunt">
    /// Whether to spend further candidates on a relation that is already placed, looking for a crossing-free
    /// route. A degraded pass turns it off: placing every relation comes before drawing any of them cleanly.
    /// </param>
    /// <returns>The routes and the failures.</returns>
    public GraphRoutingResult Route(
        IReadOnlyList<NodePlacement> nodes,
        GraphLayoutProfile profile,
        GraphPortAssignment ports,
        CancellationToken cancellation = default,
        GraphEvaluationBudget? budget = null,
        bool hunt = true)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(ports);

        return Solve(nodes, profile, ports, null, null, cancellation, budget, hunt);
    }

    /// <summary>
    /// Re-routes only the relations a change can have invalidated, and keeps every other route of the
    /// previous pass as it was. A kept route is reserved before the re-routed ones are chosen, so a
    /// re-routed relation avoids it exactly as it would in a full pass; a route the previous pass could
    /// not place, or whose ports moved, is re-routed too.
    /// </summary>
    /// <param name="nodes">The cards, by node index.</param>
    /// <param name="profile">The graph facts and the relations.</param>
    /// <param name="ports">The border ports of every relation end.</param>
    /// <param name="previous">The routes of the previous pass, one per relation; null where it placed none.</param>
    /// <param name="again">The relations to re-route.</param>
    /// <param name="cancellation">Checked between relations.</param>
    /// <param name="budget">The shared evaluation budget; a fresh one when omitted.</param>
    /// <param name="hunt">Whether to look for a crossing-free route once a relation is placed.</param>
    /// <returns>The routes and the failures, in relation-index order.</returns>
    public GraphRoutingResult Reroute(
        IReadOnlyList<NodePlacement> nodes,
        GraphLayoutProfile profile,
        GraphPortAssignment ports,
        IReadOnlyList<EdgeRoute?> previous,
        IReadOnlyList<int> again,
        CancellationToken cancellation = default,
        GraphEvaluationBudget? budget = null,
        bool hunt = true)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(again);

        return Solve(nodes, profile, ports, previous, again, cancellation, budget, hunt);
    }

    private GraphRoutingResult Solve(
        IReadOnlyList<NodePlacement> nodes,
        GraphLayoutProfile profile,
        GraphPortAssignment ports,
        IReadOnlyList<EdgeRoute?>? previous,
        IReadOnlyList<int>? again,
        CancellationToken cancellation,
        GraphEvaluationBudget? budget,
        bool hunt)
    {
        budget ??= new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations);
        var spentBefore = budget.Used;
        var edges = profile.Edges;
        if (!ports.IsComplete)
        {
            return new GraphRoutingResult(new EdgeRoute?[edges.Count], ports.Failures, [], 0, false);
        }

        var routes = new EdgeRoute?[edges.Count];
        var reserved = new GraphRouteIndex();
        var failures = new List<RouteFailure>();
        var crossings = new List<GraphCrossing>();
        var cards = new GraphCardIndex(nodes, GraphGeometryContract.CardCenterlineClearance);
        var exhausted = false;
        var ordinals = Parallel(edges);
        var due = again is null ? null : new SortedSet<int>(again);
        if (due is not null)
        {
            if (previous is null || previous.Count != edges.Count)
            {
                throw new ArgumentException("A re-route needs one previous route per relation.", nameof(previous));
            }

            if (due.Any(edge => edge < 0 || edge >= edges.Count))
            {
                throw new ArgumentOutOfRangeException(nameof(again), "A re-route set must name a relation.");
            }

            Keep(previous, due, routes, reserved, profile, ports);
        }

        var order = Order(nodes, profile, ports, ordinals);
        var pass = due is null ? order.ToArray() : order.Where(due.Contains).ToArray();
        for (var at = 0; at < pass.Length; at++)
        {
            var edge = pass[at];
            cancellation.ThrowIfCancellationRequested();

            if (exhausted)
            {
                failures.Add(new RouteFailure(edge, ERouteFailureKind.NoCandidate, [], [], []));
                continue;
            }

            var share = Share(budget, pass.Length - at);
            var verdict = Choose(edge, nodes, profile, ports, ordinals[edge], reserved, cards, share, hunt, out _);
            budget.Add(share.Used);
            exhausted = budget.IsExhausted;
            if (verdict is null)
            {
                failures.Add(new RouteFailure(edge, ERouteFailureKind.NoCandidate, [], [], []));
                continue;
            }

            if (verdict.Value.Check is null)
            {
                failures.Add(verdict.Value.Failure!);
                continue;
            }

            routes[edge] = verdict.Value.Route;
            reserved.Push(verdict.Value.Check.Flat!);
            crossings.AddRange(verdict.Value.Check.Crossings);
        }

        return new GraphRoutingResult(
            routes,
            failures.OrderBy(failure => failure.EdgeIndex).ToArray(),
            crossings.OrderBy(crossing => crossing.FirstEdge).ThenBy(crossing => crossing.SecondEdge)
                .ThenBy(crossing => crossing.Point.X).ThenBy(crossing => crossing.Point.Y).ToArray(),
            budget.Used - spentBefore,
            exhausted);
    }

    /// <summary>
    /// Reserves the routes a re-route keeps. Packing translates a whole component, so a kept route is carried
    /// into the placement it is reserved for rather than trusted as it stands: it is shifted by the distance its
    /// own child port travelled, and it is re-routed instead when that shift does not land its other end on the
    /// parent port — a card that changed shape inside the component invalidates the route, not only its ends.
    /// </summary>
    private void Keep(
        IReadOnlyList<EdgeRoute?> previous,
        SortedSet<int> due,
        EdgeRoute?[] routes,
        GraphRouteIndex reserved,
        GraphLayoutProfile profile,
        GraphPortAssignment ports)
    {
        for (var edge = 0; edge < profile.Edges.Count; edge++)
        {
            if (due.Contains(edge))
            {
                continue;
            }

            var kept = previous[edge];
            var ends = ports.Ends[edge];
            if (kept is null || ends is null || kept.Points.Count < 2)
            {
                due.Add(edge);
                continue;
            }

            var shifted = Translate(kept, new GraphPoint(ends.Child.Point.X - kept.Points[0].X, ends.Child.Point.Y - kept.Points[0].Y));
            var flat = GraphGeometry.Distance(shifted.Points[^1], ends.Parent.Point) <= KeptEndpointTolerance
                ? _validator.Flatten(shifted)
                : null;
            if (flat is null)
            {
                due.Add(edge);
                continue;
            }

            routes[edge] = shifted;
            reserved.Push(flat);
        }
    }

    /// <summary>The route with every point, the label included, carried by the given offset.</summary>
    private static EdgeRoute Translate(EdgeRoute route, GraphPoint by) =>
        by.X == 0 && by.Y == 0
            ? route
            : new EdgeRoute(
                route.EdgeIndex,
                route.Shape,
                route.Geometry,
                route.Points.Select(point => new GraphPoint(point.X + by.X, point.Y + by.Y)).ToArray(),
                new GraphPoint(route.LabelPoint.X + by.X, route.LabelPoint.Y + by.Y));

    /// <summary>
    /// The part of what is left that one relation may spend: an equal share of the budget the relations still
    /// to be routed have left between them. A relation that needs more than its share is reported unroutable
    /// and handed to the repair, instead of taking the attempts of the relations behind it.
    /// </summary>
    /// <param name="budget">The budget of the whole pass.</param>
    /// <param name="remaining">The relations still to be routed, this one included.</param>
    /// <returns>A budget the caller must fold back into the pass with <see cref="GraphEvaluationBudget.Add"/>.</returns>
    private static GraphEvaluationBudget Share(GraphEvaluationBudget budget, int remaining)
    {
        var left = budget.Limit - budget.Used;
        var allowance = Math.Max(1, left / Math.Max(1, remaining));

        return new GraphEvaluationBudget(Math.Min(left, allowance), budget.Metrics);
    }

    private (EdgeRoute? Route, GraphRouteCheck? Check, RouteFailure? Failure)? Choose(
        int edge,
        IReadOnlyList<NodePlacement> nodes,
        GraphLayoutProfile profile,
        GraphPortAssignment ports,
        (int Ordinal, int Count) family,
        GraphRouteIndex reserved,
        GraphCardIndex cardIndex,
        GraphEvaluationBudget budget,
        bool hunt,
        out bool exhausted)
    {
        exhausted = false;
        var (child, parent) = profile.Edges[edge];
        var ends = ports.Ends[edge]!;
        var own = new GraphEdgeEndpoints(child, parent);
        var candidates = Candidates(edge, nodes, profile, ends, family);
        budget.Metrics.ObserveCandidates(candidates.Count);
        var shape = ShapeOf(profile, edge);

        (EdgeRoute Route, GraphRouteCheck Check)? best = null;
        var cards = new SortedSet<int>();
        var strokes = new SortedSet<int>();
        var explored = new List<int>();

        foreach (var candidate in candidates)
        {
            if (!budget.TrySpend())
            {
                exhausted = true;
                break;
            }

            explored.Add(candidate.Ordinal);
            var route = new EdgeRoute(edge, shape, candidate.Geometry, candidate.Points, candidate.Points[0]);
            var check = _validator.Check(route, nodes, own, reserved, cardIndex);

            if (!check.IsAdmissible)
            {
                foreach (var failure in check.Failures)
                {
                    cards.UnionWith(failure.BlockingCards);
                    strokes.UnionWith(failure.BlockingRoutes);
                }

                continue;
            }

            if (best is null || check.CrossingCount < best.Value.Check.CrossingCount)
            {
                best = (route, check);
            }

            if (check.CrossingCount == 0 || !hunt)
            {
                break;
            }
        }

        if (best is { } found)
        {
            var label = GraphGeometry.ArcPoint(found.Check.Flat!.Points, 0.5);

            return (found.Route with { LabelPoint = label }, found.Check, null);
        }

        // The last phase: a relation with no clear chord and no bow that clears what stands in its way is taken
        // round the cards instead of being reported undrawable. It costs nothing unless everything above failed.
        // An ordinary relation is never bent, so only a self-reference may take it.
        if (child == parent && !exhausted && budget.TrySpend() && GraphDetourRoute.Build(ends.Child, ends.Parent, nodes, (child, parent)) is { } detour)
        {
            explored.Add(candidates.Count);
            var route = new EdgeRoute(edge, shape, EEdgeGeometry.Cubic, detour, detour[0]);
            var check = _validator.Check(route, nodes, own, reserved, cardIndex);

            if (check.IsAdmissible)
            {
                return (route with { LabelPoint = GraphGeometry.ArcPoint(check.Flat!.Points, 0.5) }, check, null);
            }

            foreach (var failure in check.Failures)
            {
                cards.UnionWith(failure.BlockingCards);
                strokes.UnionWith(failure.BlockingRoutes);
            }
        }

        var kind = cards.Count > 0 && strokes.Count == 0 ? ERouteFailureKind.CardCollision
            : strokes.Count > 0 && cards.Count == 0 ? ERouteFailureKind.StrokeOverlap
            : ERouteFailureKind.NoCandidate;

        return (null, null, new RouteFailure(edge, kind, cards.ToArray(), strokes.ToArray(), explored));
    }

    /// <summary>The meaning a relation's route carries: a self-reference, a cycle detour or a plain line.</summary>
    internal static EEdgeShape ShapeOf(GraphLayoutProfile profile, int edge)
    {
        var (child, parent) = profile.Edges[edge];

        return child == parent ? EEdgeShape.Loop
            : profile.Group[child] == profile.Group[parent] && profile.GroupSize[child] > 1 ? EEdgeShape.Detour : EEdgeShape.Line;
    }

    internal static IReadOnlyList<GraphRouteCandidate> Candidates(
        int edge,
        IReadOnlyList<NodePlacement> nodes,
        GraphLayoutProfile profile,
        GraphEdgeEnds ends,
        (int Ordinal, int Count) family)
    {
        var (child, parent) = profile.Edges[edge];
        if (child == parent)
        {
            return GraphRouteCandidates.ForLoop(ends.Child, ends.Parent);
        }

        // Every relation but a self-reference is its own chord: two ports and the line between them. It is never
        // bent; a chord a card stands on is refused by the validator, and the placement has to clear it.
        return [new GraphRouteCandidate(0, EEdgeGeometry.Straight, [ends.Child.Point, ends.Parent.Point])];
    }

    /// <summary>
    /// Self-references and parallel families first, then relations with an end on a crowded side, then the
    /// rest; inside each class the longest relation goes first, and the edge index settles every tie.
    /// </summary>
    private static IEnumerable<int> Order(
        IReadOnlyList<NodePlacement> nodes,
        GraphLayoutProfile profile,
        GraphPortAssignment ports,
        (int Ordinal, int Count)[] ordinals)
    {
        var sideLoad = new Dictionary<(int, EEdgeSide), int>();
        for (var edge = 0; edge < profile.Edges.Count; edge++)
        {
            var (child, parent) = profile.Edges[edge];
            var ends = ports.Ends[edge]!;
            sideLoad[(child, ends.Child.Side)] = sideLoad.GetValueOrDefault((child, ends.Child.Side)) + 1;
            sideLoad[(parent, ends.Parent.Side)] = sideLoad.GetValueOrDefault((parent, ends.Parent.Side)) + 1;
        }

        int Class(int edge)
        {
            var (child, parent) = profile.Edges[edge];
            if (child == parent || ordinals[edge].Count > 1)
            {
                return 0;
            }

            var ends = ports.Ends[edge]!;
            return sideLoad[(child, ends.Child.Side)] > 1 || sideLoad[(parent, ends.Parent.Side)] > 1 ? 1 : 2;
        }

        double Span(int edge) => GraphGeometry.Distance(ports.Ends[edge]!.Child.Point, ports.Ends[edge]!.Parent.Point);

        return Enumerable.Range(0, profile.Edges.Count).OrderBy(Class).ThenByDescending(Span).ThenBy(edge => edge);
    }

    /// <summary>Each relation's position within, and the size of, the family joining its pair of tables.</summary>
    internal static (int Ordinal, int Count)[] Parallel(IReadOnlyList<(int Child, int Parent)> edges)
    {
        var seen = new Dictionary<(int, int), int>();
        var result = new (int, int)[edges.Count];
        var keys = edges.Select(edge => (Math.Min(edge.Child, edge.Parent), Math.Max(edge.Child, edge.Parent))).ToArray();
        for (var edge = 0; edge < edges.Count; edge++)
        {
            seen[keys[edge]] = seen.GetValueOrDefault(keys[edge]) + 1;
        }

        var next = new Dictionary<(int, int), int>();
        for (var edge = 0; edge < edges.Count; edge++)
        {
            var ordinal = next.GetValueOrDefault(keys[edge]);
            next[keys[edge]] = ordinal + 1;
            result[edge] = (ordinal, seen[keys[edge]]);
        }

        return result;
    }
}
