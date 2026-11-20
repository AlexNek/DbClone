namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The route-substitution half of the crossing-removal search. Crossings are visited in a stable order
/// (lowest edge pair, then <c>X</c>, then <c>Y</c>), never in index-enumeration order. For each crossing
/// the alternative candidates of each of its two relations are tried, one substitution at a time in their
/// fixed ordinal order, and a substitution is kept only when the whole layout stays valid and that relation
/// crosses fewer routes than before. Placement moves, which need a fresh port and routing pass, belong to
/// <see cref="GraphLayoutRepair"/>.
/// </summary>
internal sealed class GraphCrossingRemoval
{
    private readonly GraphLayoutValidator _validator = new();

    /// <summary>Removes crossings by substituting alternative routes.</summary>
    /// <param name="candidate">A complete valid layout.</param>
    /// <param name="profile">The graph facts.</param>
    /// <param name="budget">The shared evaluation budget.</param>
    /// <param name="cancellation">Checked between substitutions.</param>
    /// <returns>The layout with the fewest crossings the substitutions reached; the input when none helped.</returns>
    public GraphLayoutCandidate Substitute(
        GraphLayoutCandidate candidate,
        GraphLayoutProfile profile,
        GraphEvaluationBudget budget,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(budget);

        var nodes = candidate.Layout.Nodes;
        var routes = candidate.Routes.ToArray();
        var reserved = new GraphRouteIndex();
        foreach (var route in routes)
        {
            reserved.Push(_validator.Flatten(route)!);
        }

        var cards = new GraphCardIndex(nodes, GraphGeometryContract.CardCenterlineClearance);
        var family = GraphNonOrthogonalRouter.Parallel(profile.Edges);
        var ends = profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray();
        var crossings = candidate.Crossings;
        var changedAny = false;

        for (var pass = 0; pass < GraphSearchBudget.MaximumCrossingPasses && !budget.IsExhausted; pass++)
        {
            cancellation.ThrowIfCancellationRequested();
            var admissible = crossings.Where(crossing => crossing.IsAdmissible).ToArray();
            if (admissible.Length == 0 || !TrySubstitute(admissible, candidate, profile, nodes, routes, reserved, cards, family, ends, budget, cancellation))
            {
                break;
            }

            changedAny = true;
            var validity = _validator.Validate(nodes, routes, ends);
            if (!validity.IsValid)
            {
                return candidate;
            }

            crossings = validity.Crossings;
        }

        return changedAny ? candidate with { Routes = routes, Crossings = crossings } : candidate;
    }

    private bool TrySubstitute(
        GraphCrossing[] admissible,
        GraphLayoutCandidate candidate,
        GraphLayoutProfile profile,
        IReadOnlyList<NodePlacement> nodes,
        EdgeRoute[] routes,
        GraphRouteIndex reserved,
        GraphCardIndex cards,
        (int Ordinal, int Count)[] family,
        GraphEdgeEndpoints[] ends,
        GraphEvaluationBudget budget,
        CancellationToken cancellation)
    {
        foreach (var crossing in admissible.OrderBy(item => item.FirstEdge).ThenBy(item => item.SecondEdge).ThenBy(item => item.Point.X).ThenBy(item => item.Point.Y))
        {
            foreach (var edge in new[] { crossing.FirstEdge, crossing.SecondEdge })
            {
                var current = admissible.Count(item => item.FirstEdge == edge || item.SecondEdge == edge);

                var alternatives = GraphNonOrthogonalRouter.Candidates(edge, nodes, profile, candidate.Ends[edge], family[edge]);
                budget.Metrics.ObserveCandidates(alternatives.Count);
                foreach (var alternative in alternatives)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!budget.TrySpend())
                    {
                        return false;
                    }

                    if (alternative.Points.SequenceEqual(routes[edge].Points))
                    {
                        continue;
                    }

                    var route = new EdgeRoute(edge, routes[edge].Shape, alternative.Geometry, alternative.Points, alternative.Points[0]);
                    var check = _validator.Check(route, nodes, ends[edge], reserved, cards);
                    if (!check.IsAdmissible || check.CrossingCount >= current)
                    {
                        continue;
                    }

                    routes[edge] = route with { LabelPoint = GraphGeometry.ArcPoint(check.Flat!.Points, 0.5) };
                    reserved.Replace(check.Flat);

                    return true;
                }
            }
        }

        return false;
    }
}
