namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Global conflict repair. A placement is evaluated by assigning ports, routing, bounded rip-up and
/// reroute, and validating. When it is not complete, repair resolves a port-capacity failure by widening
/// the card, and any other failure by moving one endpoint card of the first failed relation (away from its
/// blockers, across the relation, toward the component's centre) and keeping the move only when the
/// repair objective strictly improves. When no move helps it expands the component's canvas and starts a
/// fresh port assignment and routing pass. Intermediate states live only in memory: only a complete, valid
/// state is returned, and the search ends in an explicit failure when a budget does.
/// </summary>
internal sealed class GraphLayoutRepair
{
    private static readonly double[] ExpansionFactors = [1.3, 1.6, 2.0, 2.6];

    /// <summary>
    /// The one whole-card step a seed's crossing nudge takes while seeds still compete. Finer steps here would
    /// let one seed reach fewer crossings than another and change which arrangement the contest picks, so the
    /// contest keeps the coarse step and only a finished winner is eased further.
    /// </summary>
    private static readonly double[] ContestCrossingSteps = [1.0];

    /// <summary>
    /// The nudge lengths tried on a component's finished winner: a half-card inch, then a whole-card hop. The
    /// finer length lets a table be eased along an overlap the way a reader drags it by hand, while the
    /// neighbourhood-centroid guard keeps those small steps from pulling a hub out of the ring of tables it names.
    /// </summary>
    private static readonly double[] BeautifyCrossingSteps = [0.5, 1.0];

    /// <summary>The distance under which two card centres still count as standing on the same line.</summary>
    private const double Tolerance = 1e-6;

    private readonly GraphNonOrthogonalRouter _router = new();
    private readonly GraphConflictResolver _resolver = new();
    private readonly GraphLayoutValidator _validator = new();
    private readonly int _maximumRounds;

    /// <summary>Creates the repair.</summary>
    /// <param name="maximumRounds">The most repair rounds one seed may take.</param>
    public GraphLayoutRepair(int maximumRounds = GraphSearchBudget.MaximumRepairRounds)
    {
        _maximumRounds = maximumRounds;
    }

    /// <summary>Repairs a draft into a complete valid layout, or reports why it cannot.</summary>
    /// <param name="draft">The seed's placement.</param>
    /// <param name="profile">The graph facts.</param>
    /// <param name="budget">The shared evaluation budget.</param>
    /// <param name="cancellation">Checked between rounds and relations.</param>
    /// <param name="hunt">
    /// Whether a pass may spend further candidates on a placed relation to look for a crossing-free route. A
    /// degraded pass turns it off so its budget places every relation instead of drawing some of them cleanly.
    /// </param>
    /// <returns>The layout or the failure.</returns>
    public GraphRepairOutcome Repair(
        GraphDraftLayout draft,
        GraphLayoutProfile profile,
        GraphEvaluationBudget budget,
        CancellationToken cancellation = default,
        bool hunt = true)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(budget);

        var start = budget.Used;
        var origin = GraphPlacementState.From(draft, profile);
        var state = origin.Clone();
        var visited = new HashSet<string>(StringComparer.Ordinal) { state.Signature() };
        var movesUsed = new int[profile.ComponentNodes.Count];
        var stuck = new HashSet<int>();
        var widened = new HashSet<int>();
        var (moves, expansions) = (0, 0);
        var exhausted = new SortedSet<string>(StringComparer.Ordinal);

        try
        {
            var current = Evaluate(state, draft.Name, budget, cancellation, hunt);

            for (var round = 0; round < _maximumRounds; round++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (current.IsComplete)
                {
                    return Success(draft.Name, current, budget.Used - start, moves, expansions);
                }

                if (budget.IsExhausted)
                {
                    exhausted.Add(nameof(GraphSearchBudget.MaximumGeometryEvaluations));
                    break;
                }

                if (!current.Ports.IsComplete)
                {
                    if (!WidenForCapacity(state, current.Ports, widened))
                    {
                        // Every table the failure names has already been given all the room it can take, and no
                        // move lengthens a border: the shortage is real, so it is reported instead of grown.
                        break;
                    }

                    current = Evaluate(state, draft.Name, budget, cancellation, hunt);
                    visited.Add(state.Signature());
                    continue;
                }

                // A relation that found no route around a table already holding more ends than its border can
                // space out is short of border, not of moves: the table is given the room before any card is
                // dragged, because no drag can make a border longer.
                if (StarvedTable(state, current, widened) is { } starved)
                {
                    Widen(state, Ends(state), starved);
                    widened.Add(starved);
                    current = Evaluate(state, draft.Name, budget, cancellation, hunt);
                    visited.Add(state.Signature());
                    continue;
                }

                var improved = TryMove(state, current, visited, movesUsed, stuck, draft.Name, budget, cancellation, hunt);
                if (improved is not null)
                {
                    (state, current) = (improved.Value.State, improved.Value.Evaluation);
                    movesUsed[improved.Value.Component]++;
                    moves++;
                    budget.Metrics.ObserveRepairMove();
                    stuck.Clear();
                    continue;
                }

                if (expansions >= GraphSearchBudget.MaximumExpansions)
                {
                    exhausted.Add(nameof(GraphSearchBudget.MaximumExpansions));
                    break;
                }

                Expand(state, current, ExpansionFactors[expansions]);
                expansions++;
                stuck.Clear();
                visited.Add(state.Signature());
                current = Evaluate(state, draft.Name, budget, cancellation, hunt);
            }

            if (current.IsComplete)
            {
                return Success(draft.Name, current, budget.Used - start, moves, expansions);
            }

            if (exhausted.Count == 0)
            {
                exhausted.Add(nameof(GraphSearchBudget.MaximumRepairRounds));
            }

            return new GraphRepairOutcome(null, current.Routing.Failures.Concat(current.Ports.Failures).ToArray(), exhausted.ToArray(), budget.Used - start, false);
        }
        catch (OperationCanceledException)
        {
            return new GraphRepairOutcome(null, [], [], budget.Used - start, true);
        }
    }

    /// <summary>
    /// The placement half of the crossing-removal search: tries moving an endpoint card of the first few
    /// crossings, rebuilds ports and routes, and keeps a complete valid result only when it crosses fewer
    /// routes. Every other result is discarded, so an accepted move never costs a hard rule.
    /// </summary>
    /// <param name="candidate">A complete valid layout with crossings.</param>
    /// <param name="profile">The graph facts.</param>
    /// <param name="budget">The shared evaluation budget.</param>
    /// <param name="cancellation">Checked between trials.</param>
    /// <param name="improved">Receives complete improvements before further trials can be interrupted.</param>
    /// <returns>The layout with fewer crossings, or the input when no move helped.</returns>
    public GraphLayoutCandidate ReduceCrossingsByMoving(
        GraphLayoutCandidate candidate,
        GraphLayoutProfile profile,
        GraphEvaluationBudget budget,
        CancellationToken cancellation = default,
        Action<GraphLayoutCandidate>? improved = null) =>
        ReduceCrossingsByMoving(
            candidate, profile, budget, cancellation, ContestCrossingSteps,
            GraphSearchBudget.MaximumCrossingMoves, GraphSearchBudget.MaximumCrossingsPerMoveRound, int.MaxValue, false, improved);

    /// <summary>
    /// Eases a finished winner's crossings at sub-card steps. Ranked placements retain their neighbourhood,
    /// rank and length guards. Stars also try geometric endpoint and blocker sidesteps, including changes of
    /// sector about an attachment, and can clear one of several obstructions without needing an existing
    /// crossing. Every accepted star move keeps the hub central, the communities separated and the hidden
    /// relation count nonincreasing. Complete improvements are reported before further search, so a bounded
    /// slice cannot lose the better picture it already found. Selection has already chosen the arrangement;
    /// this pass sharpens it without substituting another seed.
    /// </summary>
    /// <param name="candidate">A complete valid layout with crossings.</param>
    /// <param name="profile">The graph facts.</param>
    /// <param name="budget">The shared evaluation budget.</param>
    /// <param name="cancellation">Checked between trials.</param>
    /// <param name="improved">Receives complete improvements before further trials can be interrupted.</param>
    /// <returns>The layout with fewer crossings or fewer hidden star chords, or the input when no nudge helped.</returns>
    public GraphLayoutCandidate BeautifyCrossings(
        GraphLayoutCandidate candidate,
        GraphLayoutProfile profile,
        GraphEvaluationBudget budget,
        CancellationToken cancellation = default,
        Action<GraphLayoutCandidate>? improved = null) =>
        ReduceCrossingsByMoving(
            candidate, profile, budget, cancellation, BeautifyCrossingSteps,
            GraphSearchBudget.MaximumBeautifyCrossingMoves, GraphSearchBudget.MaximumBeautifyCrossingsPerRound,
            GraphLayoutScore.MeasureRankViolations(profile, candidate.Layout.Nodes), true, improved);

    private GraphLayoutCandidate ReduceCrossingsByMoving(
        GraphLayoutCandidate candidate,
        GraphLayoutProfile profile,
        GraphEvaluationBudget budget,
        CancellationToken cancellation,
        double[] stepFractions,
        int maximumMoves,
        int maximumCrossingsPerRound,
        int maximumRankViolations,
        bool guardsNeighbourhood,
        Action<GraphLayoutCandidate>? improved)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(budget);

        var removal = new GraphCrossingRemoval();
        var origin = GraphPlacementState.From(candidate.Layout, profile);
        var star = candidate.Name == "clustered" ? GraphClusterStarLayout.Detect(profile) : null;
        var best = candidate;
        var state = origin.Clone();
        var visited = new HashSet<string>(StringComparer.Ordinal) { state.Signature() };

        var clearsStar = guardsNeighbourhood && star is not null;
        for (var accepted = 0; accepted < maximumMoves && !budget.IsExhausted; accepted++)
        {
            if (best.CrossingCount == 0 && (!clearsStar || GraphLayoutScore.MeasureHiddenRelations(profile, best.Layout.Nodes, best.Routes) == 0))
                break;
            (GraphPlacementState State, GraphLayoutCandidate Candidate, int Hidden, int Obstructions, double Displacement)? found = null;
            var clusteredScore = best.Name == "clustered" ? GraphLayoutScore.Of(best, profile, 0) : (GraphLayoutScore?)null;
            var hiddenBefore = clusteredScore is not null
                ? GraphLayoutScore.MeasureHiddenRelations(profile, best.Layout.Nodes, best.Routes) : 0;
            var obstructionsBefore = clearsStar ? CountObstructions(profile, best) : 0;
            var chordsOnly = best.Routes.All(route => profile.Edges[route.EdgeIndex].Child == profile.Edges[route.EdgeIndex].Parent
                || route.Geometry == EEdgeGeometry.Straight);

            var moves = CrossingMoves(state, best, stepFractions, maximumCrossingsPerRound);
            if (clearsStar) moves = StarClearanceMoves(best, profile, star!.Value.Hub).Concat(moves);
            foreach (var move in moves.DistinctBy(move => (move.Node, move.Dx, move.Dy)))
            {
                cancellation.ThrowIfCancellationRequested();
                if (budget.IsExhausted)
                {
                    break;
                }

                var trial = state.Clone();
                trial.Move(move.Node, move.Dx, move.Dy);
                trial.Settle(profile.Component[move.Node]);
                if ((guardsNeighbourhood && star is null && !StaysInItsNeighbourhood(origin, trial, move.Node, profile))
                    || !visited.Add(trial.Signature()))
                {
                    continue;
                }

                if (clearsStar)
                {
                    var placed = trial.Assemble(best.Name).Nodes;
                    if (GraphLayoutScore.MeasureClusterOverlap(profile, placed, GraphPlacementConstants.ClusterGap) > Tolerance
                        || !GraphClusterStarLayout.Preserves(candidate.Layout.Nodes, placed, star!.Value.Clusters, star.Value.Hub)
                        || (chordsOnly && !CanImproveChords(profile, placed, best.CrossingCount, hiddenBefore, obstructionsBefore)))
                        continue;
                }

                var evaluation = Evaluate(trial, best.Name, budget, cancellation, warm: Warmup(best, state));
                if (!evaluation.IsComplete)
                {
                    continue;
                }

                var next = removal.Substitute(Success(best.Name, evaluation, 0, best.Moves, best.Expansions).Candidate!, profile, budget, cancellation);
                if (star is null && GraphLayoutScore.MeasureRankViolations(profile, next.Layout.Nodes) > maximumRankViolations)
                {
                    continue;
                }

                if (clusteredScore is { } before)
                {
                    var after = GraphLayoutScore.Of(next, profile, 0);
                    if (GraphLayoutScore.MeasureClusterOverlap(profile, next.Layout.Nodes, GraphPlacementConstants.ClusterGap) > Tolerance
                        || GraphLayoutScore.MeasureHiddenRelations(profile, next.Layout.Nodes, next.Routes) > hiddenBefore
                        || (star is null && after.BlockedRelations > before.BlockedRelations)
                        || (star is null && after.RankViolations > before.RankViolations)
                        || (star is { } spokes && !GraphClusterStarLayout.Preserves(
                            candidate.Layout.Nodes, next.Layout.Nodes, spokes.Clusters, spokes.Hub))
                        || (star is null && after.TotalLength > before.TotalLength + Tolerance)
                        || (star is null && after.MaximumLength > before.MaximumLength + Tolerance))
                    {
                        continue;
                    }
                }

                var hidden = clearsStar ? GraphLayoutScore.MeasureHiddenRelations(profile, next.Layout.Nodes, next.Routes) : 0;
                var obstructions = clearsStar ? CountObstructions(profile, next) : 0;
                var quality = (next.CrossingCount, hidden, obstructions);
                var target = (found?.Candidate.CrossingCount ?? best.CrossingCount, found?.Hidden ?? (clearsStar ? hiddenBefore : 0),
                    found?.Obstructions ?? obstructionsBefore);
                var displacement = trial.DisplacementFrom(origin);
                if (quality.CompareTo(target) < 0
                    || (found is not null && quality == target && displacement < found.Value.Displacement))
                {
                    found = (trial, next, hidden, obstructions, displacement);
                    improved?.Invoke(next);
                }
            }

            if (found is null)
            {
                break;
            }

            (state, best) = (found.Value.State, found.Value.Candidate);
            budget.Metrics.ObserveRepairMove();
        }

        return best;
    }

    /// <summary>Rejects unhelpful straight-chord trials before spending routing work on the self-loops.</summary>
    private static bool CanImproveChords(GraphLayoutProfile profile, IReadOnlyList<NodePlacement> cards,
        int crossingLimit, int hiddenLimit, int obstructionLimit)
    {
        var ports = GraphPortAssigner.Assign(cards, profile.Edges);
        if (!ports.IsComplete) return false;
        var edges = Enumerable.Range(0, profile.Edges.Count)
            .Where(edge => profile.Edges[edge].Child != profile.Edges[edge].Parent).ToArray();
        var crossings = 0;
        var hidden = 0;
        var obstructions = 0;
        for (var at = 0; at < edges.Length; at++)
        {
            var edge = profile.Edges[edges[at]];
            var ends = ports.Ends[edges[at]]!;
            var blockers = cards.Count(card => card.NodeIndex != edge.Child && card.NodeIndex != edge.Parent
                && GraphGeometry.SegmentHitsCardEnvelope(ends.Child.Point, ends.Parent.Point, card));
            obstructions += blockers;
            if (blockers > 0) hidden++;
            if (hidden > hiddenLimit) return false;
            for (var next = at + 1; next < edges.Length; next++)
            {
                var other = ports.Ends[edges[next]]!;
                if (GraphGeometry.SegmentsCross(ends.Child.Point, ends.Parent.Point, other.Child.Point, other.Parent.Point)) crossings++;
                if (crossings > crossingLimit) return false;
            }
        }
        return crossings < crossingLimit || hidden < hiddenLimit || obstructions < obstructionLimit;
    }

    private int CountObstructions(GraphLayoutProfile profile, GraphLayoutCandidate candidate) =>
        candidate.Routes.Sum(route =>
        {
            var edge = profile.Edges[route.EdgeIndex];
            if (edge.Child == edge.Parent) return 0;
            var line = _validator.Flatten(route)!.Points;
            return candidate.Layout.Nodes.Count(card => card.NodeIndex != edge.Child && card.NodeIndex != edge.Parent
                && Enumerable.Range(1, line.Count - 1).Any(at => GraphGeometry.SegmentHitsCardEnvelope(line[at - 1], line[at], card)));
        });

    /// <summary>Geometric sidesteps for hidden chords and crossing endpoints, without moving the shared hub.</summary>
    private static IEnumerable<GraphRepairMove> StarClearanceMoves(GraphLayoutCandidate candidate, GraphLayoutProfile profile, int hub)
    {
        var cards = candidate.Layout.Nodes;
        var ordinal = 0;
        foreach (var node in Enumerable.Range(0, cards.Count).Where(node => node != hub))
        {
            var clearances = GraphStarPlacementMoves.Clearances(profile, cards, node).ToArray();
            foreach (var delta in clearances)
                yield return new GraphRepairMove(node, delta.X, delta.Y, ordinal++);
            if (clearances.Length == 0 || profile.Neighbours[node].Count == 0) continue;
            var family = profile.ClusterGroups.First(group => group.Contains(node));
            var attachment = profile.Neighbours[node].OrderByDescending(family.Contains)
                .ThenByDescending(peer => profile.Neighbours[peer].Count).ThenBy(peer => peer).First();
            foreach (var delta in GraphStarPlacementMoves.Orbit(cards, new[] { node }, attachment))
                yield return new GraphRepairMove(node, delta.X, delta.Y, ordinal++);
        }
        foreach (var node in Enumerable.Range(0, cards.Count).Where(node => node != hub))
            foreach (var delta in GraphStarPlacementMoves.Endpoints(profile, cards, node))
                yield return new GraphRepairMove(node, delta.X, delta.Y, ordinal++);
    }

    /// <summary>Trial moves for the endpoint cards of the first few crossings: away from the crossing and across each relation, at each nudge length.</summary>
    private static IEnumerable<GraphRepairMove> CrossingMoves(
        GraphPlacementState state,
        GraphLayoutCandidate candidate,
        double[] stepFractions,
        int maximumCrossingsPerRound)
    {
        var step = GraphCard.Height + GraphPlacementConstants.CardGap;
        var ordinal = 0;
        var seen = new HashSet<(int, int)>();

        foreach (var crossing in candidate.Crossings.Where(item => item.IsAdmissible)
            .OrderBy(item => item.FirstEdge).ThenBy(item => item.SecondEdge).ThenBy(item => item.Point.X).ThenBy(item => item.Point.Y))
        {
            if (!seen.Add((crossing.FirstEdge, crossing.SecondEdge)) || seen.Count > maximumCrossingsPerRound)
            {
                continue;
            }

            var nodes = new[] { crossing.FirstEdge, crossing.SecondEdge }
                .SelectMany(edge => new[] { state.Profile.Edges[edge].Child, state.Profile.Edges[edge].Parent })
                .Distinct().OrderBy(node => node).ToArray();

            foreach (var node in nodes)
            {
                var away = Unit(state.CentreX[node] - crossing.Point.X, state.CentreY[node] - crossing.Point.Y);
                foreach (var fraction in stepFractions)
                {
                    var length = step * fraction;
                    foreach (var (x, y) in new[] { (away.X, away.Y), (-away.Y, away.X), (away.Y, -away.X) })
                    {
                        if (x != 0 || y != 0)
                        {
                            yield return new GraphRepairMove(node, x * length, y * length, ordinal);
                        }

                        ordinal++;
                    }
                }
            }
        }
    }
    /// <summary>
    /// Whether a trial move keeps its card no farther from the centre of the neighbourhood it shares relations
    /// with than the placement it started from. A crossing nudge may slide a card around the ring of tables
    /// around it, but must never pull a hub out of the children it names — the trade the crossing count alone
    /// would make. The neighbourhood centre is taken from the origin, so accepted moves cannot add up into a
    /// drift away from where the ring began.
    /// </summary>
    private static bool StaysInItsNeighbourhood(
        GraphPlacementState origin,
        GraphPlacementState trial,
        int node,
        GraphLayoutProfile profile)
    {
        var neighbours = profile.Neighbours[node];
        if (neighbours.Count == 0)
        {
            return true;
        }

        var centreX = neighbours.Average(item => origin.CentreX[item]);
        var centreY = neighbours.Average(item => origin.CentreY[item]);
        var was = Distance(origin.CentreX[node], origin.CentreY[node], centreX, centreY);
        var now = Distance(trial.CentreX[node], trial.CentreY[node], centreX, centreY);

        return now <= was + GraphPlacementConstants.CardGap;
    }

    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));

    /// <summary>
    /// Assigns ports, routes, resolves conflicts and validates one placement. With a warm-up the move is
    /// re-routed locally along the relations it can have invalidated (§9) instead of the whole component.
    /// </summary>
    private GraphRepairEvaluation Evaluate(
        GraphPlacementState state,
        string name,
        GraphEvaluationBudget budget,
        CancellationToken cancellation,
        bool hunt = true,
        GraphRouteWarmup? warm = null)
    {
        var layout = state.Assemble(name);
        var profile = state.Profile;
        var ports = GraphPortAssigner.Assign(layout.Nodes, profile.Edges);

        if (!ports.IsComplete)
        {
            var none = new GraphRoutingResult(new EdgeRoute?[profile.Edges.Count], ports.Failures, [], 0, false);

            return new GraphRepairEvaluation(layout, ports, none, [], new GraphRepairObjective(ports.Failures.Count, profile.Edges.Count, 0));
        }

        var first = warm is null
            ? _router.Route(layout.Nodes, profile, ports, cancellation, budget, hunt)
            : _router.Reroute(
                layout.Nodes,
                profile,
                ports,
                warm.Routes,
                GraphRerouteSet.Of(warm, layout.Nodes, state, ports, profile),
                cancellation,
                budget,
                hunt);
        var routing = first.IsComplete ? first : _resolver.Resolve(layout.Nodes, profile, ports, first, budget, cancellation);
        var accepted = routing.Routes.Where(route => route is not null).Select(route => route!).ToArray();
        var validity = _validator.Validate(layout.Nodes, accepted, profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());
        var blockers = routing.Failures.Sum(failure => failure.BlockingCards.Count + failure.BlockingRoutes.Count) + validity.Failures.Count;

        return new GraphRepairEvaluation(layout, ports, routing, validity.Crossings, new GraphRepairObjective(0, routing.Failures.Count, blockers));
    }

    /// <summary>The warm-up that lets a trial re-route only the relations it invalidated.</summary>
    private static GraphRouteWarmup Warmup(GraphLayoutCandidate candidate, GraphPlacementState before) =>
        new(before,
            candidate.Layout.Nodes,
            candidate.Ends.Select(end => (GraphEdgeEnds?)end).ToArray(),
            candidate.Routes.Select(route => (EdgeRoute?)route).ToArray());

    /// <summary>The warm-up that lets a trial re-route only the relations it invalidated.</summary>
    private static GraphRouteWarmup Warmup(GraphRepairEvaluation evaluation, GraphPlacementState before) =>
        new(before, evaluation.Layout.Nodes, evaluation.Ports.Ends, evaluation.Routing.Routes);

    private static GraphRepairOutcome Success(string name, GraphRepairEvaluation evaluation, int spent, int moves, int expansions) =>
        new(
            new GraphLayoutCandidate(
                name,
                evaluation.Layout,
                evaluation.Ports.Ends.Select(ends => ends!).ToArray(),
                evaluation.Routing.Routes.Select(route => route!).ToArray(),
                evaluation.Crossings,
                spent,
                moves,
                expansions),
            [],
            [],
            spent,
            false);

    /// <summary>
    /// A capacity failure is resolved by a wider card, because no move changes a border's length. A table is
    /// widened once per repair: an endless round of widening would turn one card into a band across the picture.
    /// </summary>
    /// <param name="state">The placement being repaired.</param>
    /// <param name="ports">Its port assignment, which names the tables that ran out of border.</param>
    /// <param name="widened">The tables already given room.</param>
    /// <returns>Whether any table was given room.</returns>
    private static bool WidenForCapacity(GraphPlacementState state, GraphPortAssignment ports, HashSet<int> widened)
    {
        var ends = Ends(state);
        var grew = false;

        foreach (var node in ports.Failures.SelectMany(failure => failure.BlockingCards).Distinct().OrderBy(node => node))
        {
            if (!widened.Add(node))
            {
                continue;
            }

            Widen(state, ends, node);
            grew = true;
        }

        return grew;
    }

    /// <summary>
    /// A relation with no route at all can mean the table it enters has no border left: a table holding more
    /// relations than its sides can space out cannot host one more, and moving cards never changes that. The
    /// busiest table of the failed relations that has not been widened yet gets the room it lacks; a table is
    /// widened once per repair, so a picture is never grown for a problem room cannot solve.
    /// </summary>
    /// <param name="state">The placement being repaired.</param>
    /// <param name="current">Its evaluation.</param>
    /// <param name="widened">The tables already given room, so the guess is made once each.</param>
    /// <returns>The table to widen, or null when the failures are not a shortage of border.</returns>
    private static int? StarvedTable(GraphPlacementState state, GraphRepairEvaluation current, HashSet<int> widened)
    {
        var ends = Ends(state);

        foreach (var node in current.Routing.Failures
            .Where(failure => failure.Kind == ERouteFailureKind.NoCandidate)
            .SelectMany(failure => new[] { state.Profile.Edges[failure.EdgeIndex].Child, state.Profile.Edges[failure.EdgeIndex].Parent })
            .Distinct()
            .Where(node => !widened.Contains(node))
            .OrderByDescending(node => ends[node])
            .ThenBy(node => node))
        {
            // Only a table that already holds more ends than one row of ports can space out is out of border.
            if (ends[node] * GraphGeometryContract.MinimumPortStep > state.Widths[node] + GraphCard.Height)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>The number of ends meeting each table.</summary>
    private static int[] Ends(GraphPlacementState state)
    {
        var ends = new int[state.Widths.Length];
        foreach (var (child, parent) in state.Profile.Edges)
        {
            ends[child]++;
            ends[parent]++;
        }

        return ends;
    }

    /// <summary>Gives one table the width its ends need, never less than one more port step.</summary>
    private static void Widen(GraphPlacementState state, int[] ends, int node)
    {
        var needed = Math.Max(GraphPortAssigner.MinimumWidth(ends[node], GraphCard.Height), state.Widths[node] + (2 * GraphGeometryContract.MinimumPortStep));
        state.Widen(node, needed);
        state.Settle(state.Profile.Component[node]);
    }

    /// <summary>
    /// Tries the moves of the first failed relation that has not been tried in this position, and keeps the
    /// first one that strictly improves the objective. The moves come nearest and simplest first — one step
    /// clear of the blockers before two, a side of the relation before the pull inward — and each trial costs a
    /// whole route pass, so the search takes the first help it finds instead of paying for a better one: a
    /// picture restored inside the watchdog is worth more than an optimal move the reader never waits for.
    /// </summary>
    private (GraphPlacementState State, GraphRepairEvaluation Evaluation, int Component)? TryMove(
        GraphPlacementState state,
        GraphRepairEvaluation current,
        HashSet<string> visited,
        int[] movesUsed,
        HashSet<int> stuck,
        string name,
        GraphEvaluationBudget budget,
        CancellationToken cancellation,
        bool hunt)
    {
        var star = name == "clustered" ? GraphClusterStarLayout.Detect(state.Profile) : null;
        foreach (var failure in current.Routing.Failures.OrderBy(failure => failure.EdgeIndex))
        {
            if (stuck.Contains(failure.EdgeIndex))
            {
                continue;
            }

            (GraphPlacementState State, GraphRepairEvaluation Evaluation, int Component)? chosen = null;
            foreach (var move in Moves(state, failure))
            {
                var component = state.Profile.Component[move.Node];
                if (movesUsed[component] >= GraphSearchBudget.MaximumRepairMovesPerComponent || budget.IsExhausted)
                {
                    continue;
                }

                var trial = state.Clone();
                trial.Move(move.Node, move.Dx, move.Dy);
                trial.Settle(component);
                if (!visited.Add(trial.Signature()))
                {
                    continue;
                }

                var evaluation = Evaluate(trial, name, budget, cancellation, hunt: hunt, warm: Warmup(current, state));
                if (name == "clustered" && (GraphLayoutScore.MeasureClusterOverlap(
                    state.Profile, evaluation.Layout.Nodes, GraphPlacementConstants.ClusterGap) > Tolerance
                    || (star is { } spokes && !GraphClusterStarLayout.Preserves(
                        current.Layout.Nodes, evaluation.Layout.Nodes, spokes.Clusters, spokes.Hub))))
                {
                    continue;
                }

                if (evaluation.Objective.CompareTo(current.Objective) < 0)
                {
                    chosen = (trial, evaluation, component);
                    break;
                }
            }

            if (chosen is { } accepted)
            {
                return accepted;
            }

            stuck.Add(failure.EdgeIndex);
        }

        return null;
    }

    /// <summary>
    /// The deterministic trial moves for the endpoints of a failed relation: away from its blocking cards,
    /// either side of the relation, and toward the component's centre, in steps of one card plus a lane.
    /// </summary>
    private static IEnumerable<GraphRepairMove> Moves(GraphPlacementState state, RouteFailure failure)
    {
        var (child, parent) = state.Profile.Edges[failure.EdgeIndex];
        var step = GraphCard.Height + GraphPlacementConstants.CardGap;
        var ordinal = 0;

        foreach (var node in new[] { child, parent }.Distinct().OrderBy(node => node))
        {
            var directions = new List<(double X, double Y, double Step)>();
            if (failure.BlockingCards.Count > 0)
            {
                var (bx, by) = (failure.BlockingCards.Average(card => state.CentreX[card]), failure.BlockingCards.Average(card => state.CentreY[card]));
                var away = Unit(state.CentreX[node] - bx, state.CentreY[node] - by);
                directions.Add((away.X, away.Y, step));
                directions.Add((away.X, away.Y, 2 * step));
            }

            var chord = Unit(state.CentreX[parent] - state.CentreX[child], state.CentreY[parent] - state.CentreY[child]);
            directions.Add((-chord.Y, chord.X, step));
            directions.Add((chord.Y, -chord.X, step));

            var members = state.Profile.ComponentNodes[state.Profile.Component[node]];
            var inward = Unit(members.Average(member => state.CentreX[member]) - state.CentreX[node], members.Average(member => state.CentreY[member]) - state.CentreY[node]);
            directions.Add((inward.X, inward.Y, step));

            foreach (var (x, y, length) in directions)
            {
                if (x != 0 || y != 0)
                {
                    yield return new GraphRepairMove(node, x * length, y * length, ordinal);
                }

                ordinal++;
            }
        }
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        var length = Math.Sqrt((x * x) + (y * y));

        return length < 1e-9 ? (0, 0) : (x / length, y / length);
    }

    /// <summary>Expands every component that still has a failed relation; the router never expands the canvas.</summary>
    private static void Expand(GraphPlacementState state, GraphRepairEvaluation current, double factor)
    {
        var failing = current.Routing.Failures
            .Select(failure => state.Profile.Component[state.Profile.Edges[failure.EdgeIndex].Child])
            .Distinct().OrderBy(component => component);

        foreach (var component in failing)
        {
            state.Expand(component, factor);
        }
    }
}


