namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Bounded rip-up and reroute. A relation that found no admissible route is the seed of a conflict set: the
/// routes that block its candidates, capped at <see cref="GraphSearchBudget.MaximumConflictSetSize"/>. The
/// whole set is lifted out of the reserved routes and searched together, constrained relation first, then by
/// edge index, over at most <see cref="GraphSearchBudget.MaximumConflictAssignments"/> assignments. An
/// assignment is committed only when every relation of the set is valid against the routes outside the set
/// and against the others in it; a partial set is never committed and an overlapping route never accepted.
/// </summary>
internal sealed class GraphConflictResolver
{
    private readonly GraphLayoutValidator _validator = new();

    /// <summary>Tries to route the relations the first pass could not.</summary>
    /// <param name="nodes">The cards, by node index.</param>
    /// <param name="profile">The graph facts and the relations.</param>
    /// <param name="ports">The border ports of every relation end.</param>
    /// <param name="initial">The first routing pass.</param>
    /// <param name="budget">The shared evaluation budget.</param>
    /// <param name="cancellation">Checked between conflict sets.</param>
    /// <returns>The routes and the failures that remain.</returns>
    public GraphRoutingResult Resolve(
        IReadOnlyList<NodePlacement> nodes,
        GraphLayoutProfile profile,
        GraphPortAssignment ports,
        GraphRoutingResult initial,
        GraphEvaluationBudget budget,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(budget);

        var routes = initial.Routes.ToArray();
        var failures = initial.Failures.ToDictionary(failure => failure.EdgeIndex);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var spentBefore = budget.Used;
        var family = GraphNonOrthogonalRouter.Parallel(profile.Edges);

        // The flattening of a route and the candidates of a relation depend on the placement and the ports,
        // which this resolve never changes, so both are paid for once and shared by every conflict set. A
        // committed route replaces its flattening; the candidates never change.
        var flats = new Dictionary<int, GraphFlatRoute>();
        var candidates = new Dictionary<int, IReadOnlyList<GraphRouteCandidate>>();

        for (var round = 0; round < GraphSearchBudget.MaximumRipUpRounds && !budget.IsExhausted; round++)
        {
            var progress = false;
            var reserved = new GraphRouteIndex();
            for (var edge = 0; edge < routes.Length; edge++)
            {
                if (routes[edge] is { } route && FlatOf(route) is { } flat)
                {
                    reserved.Push(flat);
                }
            }

            foreach (var failure in failures.Values.Where(failure => failure.BlockingRoutes.Count > 0).OrderBy(failure => failure.EdgeIndex).ToArray())
            {
                cancellation.ThrowIfCancellationRequested();
                if (budget.IsExhausted || !failures.ContainsKey(failure.EdgeIndex))
                {
                    continue;
                }

                var search = new GraphConflictSearch(_validator, nodes, profile, ports, family, routes, budget, reserved, flats, candidates);
                var assigned = search.Solve(failure, attempted);
                if (assigned is null)
                {
                    continue;
                }

                foreach (var (edge, route) in assigned)
                {
                    routes[edge] = route;
                    if (_validator.Flatten(route) is { } flat)
                    {
                        flats[edge] = flat;
                        reserved.Replace(flat);
                    }
                }

                failures.Remove(failure.EdgeIndex);
                progress = true;
            }

            if (!progress)
            {
                break;
            }
        }

        return new GraphRoutingResult(
            routes,
            failures.Values.OrderBy(failure => failure.EdgeIndex).ToArray(),
            initial.Crossings,
            initial.GeometryEvaluations + (budget.Used - spentBefore),
            initial.BudgetExhausted || budget.IsExhausted);

        GraphFlatRoute? FlatOf(EdgeRoute route)
        {
            if (flats.TryGetValue(route.EdgeIndex, out var known))
            {
                return known;
            }

            var flat = _validator.Flatten(route);
            if (flat is not null)
            {
                flats[route.EdgeIndex] = flat;
            }

            return flat;
        }
    }
}
