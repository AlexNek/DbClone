namespace DbClone.UI.Services.GraphLayout;

/// <summary>One conflict-set search over a fixed set of routes: the failed relation and the routes blocking it are searched together.</summary>
internal sealed class GraphConflictSearch
{
    private readonly GraphLayoutValidator _validator;
    private readonly IReadOnlyList<NodePlacement> _nodes;
    private readonly GraphLayoutProfile _profile;
    private readonly GraphPortAssignment _ports;
    private readonly (int Ordinal, int Count)[] _family;
    private readonly EdgeRoute?[] _routes;
    private readonly GraphEvaluationBudget _budget;
    private readonly GraphCardIndex _cards;
    private readonly GraphRouteIndex _index;
    private readonly Dictionary<int, GraphFlatRoute> _flats;
    private readonly Dictionary<int, IReadOnlyList<GraphRouteCandidate>> _candidates;

    /// <summary>The relations whose reserved route must not be counted: they are being replaced by this search.</summary>
    private readonly HashSet<int> _mask = [];

    private int _assignments;

    public GraphConflictSearch(
        GraphLayoutValidator validator,
        IReadOnlyList<NodePlacement> nodes,
        GraphLayoutProfile profile,
        GraphPortAssignment ports,
        (int Ordinal, int Count)[] family,
        EdgeRoute?[] routes,
        GraphEvaluationBudget budget,
        GraphRouteIndex index,
        Dictionary<int, GraphFlatRoute> flats,
        Dictionary<int, IReadOnlyList<GraphRouteCandidate>> candidates)
    {
        _validator = validator;
        _nodes = nodes;
        _profile = profile;
        _ports = ports;
        _family = family;
        _routes = routes;
        _budget = budget;
        _index = index;
        _flats = flats;
        _candidates = candidates;
        _cards = new GraphCardIndex(nodes, GraphGeometryContract.CardCenterlineClearance);
    }

    /// <summary>Finds an assignment for the failed relation and the routes blocking it, or null.</summary>
    public Dictionary<int, EdgeRoute>? Solve(RouteFailure failure, HashSet<string> attempted)
    {
        var members = new SortedSet<int>(failure.BlockingRoutes.Where(edge => _routes[edge] is not null));
        var truncated = Truncate(failure, members);
        var floor = _index.Count;

        try
        {
            for (var growth = 0; growth < 3; growth++)
            {
                var key = failure.EdgeIndex + ":" + string.Join(",", members);
                if (!attempted.Add(key))
                {
                    return null;
                }

                _assignments = 0;
                var order = new List<int> { failure.EdgeIndex };
                order.AddRange(members);
                _budget.Metrics.ObserveConflictSet(order.Count);

                // The set's own routes are lifted out of the index by the mask, and each is put back the
                // moment the search reserves its replacement, so a candidate is judged against the routes
                // outside the set and against the ones the search already chose.
                _mask.Clear();
                _mask.UnionWith(members);
                _mask.Add(failure.EdgeIndex);

                var discovered = new SortedSet<int>();
                var chosen = new Dictionary<int, EdgeRoute>();

                if (Dfs(0, order, chosen, discovered))
                {
                    return chosen;
                }

                // A truncated set is not treated as solved: the placement repair takes over. An untruncated
                // set grows by the outside routes that blocked the failed relation, up to the cap.
                var grown = discovered.Where(edge => !members.Contains(edge)).ToArray();
                if (truncated || grown.Length == 0 || _budget.IsExhausted
                    || members.Count + grown.Length > GraphSearchBudget.MaximumConflictSetSize - 1)
                {
                    return null;
                }

                members.UnionWith(grown);
            }

            return null;
        }
        finally
        {
            // The chosen routes are the answer's, not the index's: the caller reserves them once committed.
            _mask.Clear();
            while (_index.Count > floor)
            {
                _index.Pop();
            }
        }
    }

    /// <summary>Keeps the failed relation and the blockers that block the most of its candidates, ties by edge index.</summary>
    private bool Truncate(RouteFailure failure, SortedSet<int> members)
    {
        if (members.Count < GraphSearchBudget.MaximumConflictSetSize)
        {
            return false;
        }

        var reserved = members.Select(edge => FlatOf(_routes[edge]!)).Where(flat => flat is not null).Select(flat => flat!).ToList();
        var contribution = members.ToDictionary(edge => edge, _ => 0);
        foreach (var candidate in Candidates(failure.EdgeIndex))
        {
            if (!_budget.TrySpend())
            {
                break;
            }

            foreach (var blocker in Check(failure.EdgeIndex, candidate, reserved).Failures.SelectMany(item => item.BlockingRoutes).Distinct())
            {
                contribution[blocker]++;
            }
        }

        var keep = members.OrderByDescending(edge => contribution[edge]).ThenBy(edge => edge)
            .Take(GraphSearchBudget.MaximumConflictSetSize - 1).ToArray();
        members.Clear();
        members.UnionWith(keep);

        return true;
    }

    private bool Dfs(int level, List<int> order, Dictionary<int, EdgeRoute> chosen, SortedSet<int> discovered)
    {
        if (level == order.Count)
        {
            return true;
        }

        var edge = order[level];
        var branches = 0;

        foreach (var candidate in Candidates(edge))
        {
            if (++_assignments > GraphSearchBudget.MaximumConflictAssignments || !_budget.TrySpend())
            {
                return false;
            }

            var check = Check(edge, candidate, _index);
            if (!check.IsAdmissible)
            {
                if (level == 0)
                {
                    discovered.UnionWith(check.Failures.SelectMany(failure => failure.BlockingRoutes));
                }

                continue;
            }

            var route = Route(edge, candidate);
            _index.Push(check.Flat!);
            _mask.Remove(edge);
            chosen[edge] = route;
            if (Dfs(level + 1, order, chosen, discovered))
            {
                return true;
            }

            _index.Pop();
            _mask.Add(edge);
            chosen.Remove(edge);
            if (++branches >= GraphSearchBudget.ConflictBranchLimit)
            {
                break;
            }
        }

        return false;
    }

    private IReadOnlyList<GraphRouteCandidate> Candidates(int edge)
    {
        if (!_candidates.TryGetValue(edge, out var candidates))
        {
            _candidates[edge] = candidates = GraphNonOrthogonalRouter.Candidates(edge, _nodes, _profile, _ports.Ends[edge]!, _family[edge]);
        }

        _budget.Metrics.ObserveCandidates(candidates.Count);
        return candidates;
    }

    private EdgeRoute Route(int edge, GraphRouteCandidate candidate) =>
        new(edge, GraphNonOrthogonalRouter.ShapeOf(_profile, edge), candidate.Geometry, candidate.Points, candidate.Points[0]);

    private GraphRouteCheck Check(int edge, GraphRouteCandidate candidate, IReadOnlyList<GraphFlatRoute> reserved)
    {
        var (child, parent) = _profile.Edges[edge];

        return _validator.Check(Route(edge, candidate), _nodes, new GraphEdgeEndpoints(child, parent), reserved, _cards);
    }

    private GraphRouteCheck Check(int edge, GraphRouteCandidate candidate, GraphRouteIndex reserved)
    {
        var (child, parent) = _profile.Edges[edge];

        return _validator.Check(Route(edge, candidate), _nodes, new GraphEdgeEndpoints(child, parent), reserved, _cards, _mask);
    }

    private GraphFlatRoute? FlatOf(EdgeRoute route)
    {
        if (!_flats.TryGetValue(route.EdgeIndex, out var flat))
        {
            return _validator.Flatten(route);
        }

        return flat;
    }
}
