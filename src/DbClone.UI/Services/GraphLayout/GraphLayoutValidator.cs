namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The hard-validity checks of one candidate layout: finite coordinates, adequate flattening, a valid
/// approach at each port, a route that does not cross itself, no collision with any card — the two the route
/// joins included, apart from the port it attaches at — and no shared or near-parallel run between two
/// routes. A crossing at one point is classified and counted; it is not an overlap. Nothing here is a
/// weighted cost: a layout with one failure is invalid however short it is.
/// <para>
/// A chord — a relation drawn as the one straight segment between its two ports — is held to less than that:
/// its ends must sit on the borders of the cards it joins, and it must stay finite, but the cards are painted
/// over its ink. A chord may pass behind a table, leave a border at any angle, and run beside or across
/// another chord; that is what a catalog of ranks looks like. Every meeting between chords is still counted
/// as a crossing, so the score can prefer the arrangement that crosses least. A curve keeps every rule,
/// because nothing hides it where it wanders.
/// </para>
/// </summary>
internal sealed class GraphLayoutValidator
{
    /// <summary>How far a port may sit from its card's border and still count as on it.</summary>
    private const double BorderTolerance = 0.5;

    /// <summary>
    /// How far a route may enter a card it joins before that counts as passing through it. A port sits on the
    /// border, so its own stroke grazes the edge it leaves from; anything deeper than this is the route running
    /// over the card, which no arrangement may produce.
    /// </summary>
    private const double OwnCardInset = 2;

    /// <summary>The smallest sine of the angle between a route's end direction and the border (about five degrees).</summary>
    private const double MinimumApproachSine = 0.087;

    /// <summary>The distance between two samples taken along a route when measuring a shared run.</summary>
    private const double SampleStep = GraphGeometryContract.MinimumCenterlineDistance / 2;

    /// <summary>Two meetings nearer than this are one crossing found twice at a segment join.</summary>
    private const double SamePoint = 1e-6;

    /// <summary>Validates a layout.</summary>
    /// <param name="nodes">The cards, indexed by node index.</param>
    /// <param name="routes">The routes, one per relation.</param>
    /// <param name="ends">The two cards each relation joins, indexed by relation index.</param>
    /// <returns>The failures and the crossings, in a stable order.</returns>
    public GraphLayoutValidity Validate(
        IReadOnlyList<NodePlacement> nodes,
        IReadOnlyList<EdgeRoute> routes,
        IReadOnlyList<GraphEdgeEndpoints> ends)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(ends);

        var failures = new List<RouteFailure>();
        var flats = new List<GraphFlatRoute>();
        var cards = new GraphCardIndex(nodes, GraphGeometryContract.CardCenterlineClearance);

        foreach (var route in routes)
        {
            var flat = Prepare(route, failures);
            if (flat is null)
            {
                continue;
            }

            var own = ends[route.EdgeIndex];
            if (flat.IsChord)
            {
                CheckEnds(flat, nodes[own.ChildNode], nodes[own.ParentNode], failures);
            }
            else
            {
                CheckApproach(route, flat, nodes[own.ChildNode], nodes[own.ParentNode], failures);
                CheckCards(flat, nodes, own, cards, failures);
                CheckSelfIntersection(flat, route, failures);
            }

            flats.Add(flat);
        }

        var crossings = new List<GraphCrossing>();
        CheckPairs(flats, failures, crossings);

        return new GraphLayoutValidity(
            failures.OrderBy(failure => failure.EdgeIndex).ThenBy(failure => failure.Kind).ToArray(),
            crossings.OrderBy(crossing => crossing.FirstEdge).ThenBy(crossing => crossing.SecondEdge)
                .ThenBy(crossing => crossing.Point.X).ThenBy(crossing => crossing.Point.Y).ToArray());
    }

    /// <summary>
    /// Checks one candidate route against every card and against the routes already reserved, so a router
    /// can reject it before it is committed. Failures name the candidate's own edge.
    /// </summary>
    /// <param name="route">The candidate.</param>
    /// <param name="nodes">The cards, by node index.</param>
    /// <param name="own">The two cards the candidate joins.</param>
    /// <param name="reserved">The flattened routes already accepted.</param>
    /// <param name="cards">
    /// The card index of the placement, when the caller checks many candidates against one placement; one is
    /// built for the call otherwise.
    /// </param>
    /// <returns>The verdict.</returns>
    public GraphRouteCheck Check(
        EdgeRoute route,
        IReadOnlyList<NodePlacement> nodes,
        GraphEdgeEndpoints own,
        IReadOnlyList<GraphFlatRoute> reserved,
        GraphCardIndex? cards = null)
    {
        ArgumentNullException.ThrowIfNull(reserved);

        return Check(route, nodes, own, cards, flat => reserved.Where(other => other.Near(flat, GraphGeometryContract.MinimumCenterlineDistance)));
    }

    /// <summary>
    /// Checks one candidate route against every card and against the routes an index holds, so a router
    /// checks the routes beside the candidate instead of every route reserved. The candidate's own relation
    /// is left out of the answer.
    /// </summary>
    /// <param name="route">The candidate.</param>
    /// <param name="nodes">The cards, by node index.</param>
    /// <param name="own">The two cards the candidate joins.</param>
    /// <param name="reserved">The index of the routes already accepted.</param>
    /// <param name="cards">
    /// The card index of the placement, when the caller checks many candidates against one placement; one is
    /// built for the call otherwise.
    /// </param>
    /// <param name="excluded">
    /// Relation indexes the check must ignore — the candidate's own relation is always ignored, and a search
    /// that lifts a set of routes adds the routes it is replacing.
    /// </param>
    /// <returns>The verdict.</returns>
    public GraphRouteCheck Check(
        EdgeRoute route,
        IReadOnlyList<NodePlacement> nodes,
        GraphEdgeEndpoints own,
        GraphRouteIndex reserved,
        GraphCardIndex? cards = null,
        IReadOnlyCollection<int>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(reserved);

        return Check(route, nodes, own, cards, flat => reserved.Near(flat, GraphGeometryContract.MinimumCenterlineDistance, excluded ?? [flat.EdgeIndex]));
    }

    private GraphRouteCheck Check(
        EdgeRoute route,
        IReadOnlyList<NodePlacement> nodes,
        GraphEdgeEndpoints own,
        GraphCardIndex? cards,
        Func<GraphFlatRoute, IEnumerable<GraphFlatRoute>> near)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(own);

        var failures = new List<RouteFailure>();
        var flat = Prepare(route, failures);
        if (flat is null)
        {
            return new GraphRouteCheck(null, failures, []);
        }

        if (!flat.IsChord)
        {
            CheckApproach(route, flat, nodes[own.ChildNode], nodes[own.ParentNode], failures);
            CheckCards(flat, nodes, own, cards ?? new GraphCardIndex(nodes, GraphGeometryContract.CardCenterlineClearance), failures);
            CheckSelfIntersection(flat, route, failures);
        }
        else
        {
            CheckEnds(flat, nodes[own.ChildNode], nodes[own.ParentNode], failures);
        }

        var crossings = new List<GraphCrossing>();
        foreach (var other in near(flat))
        {
            var pair = new List<RouteFailure>();
            var (low, high) = other.EdgeIndex < flat.EdgeIndex ? (other, flat) : (flat, other);
            CheckPair(low, high, pair, crossings);
            failures.AddRange(pair.Select(failure => new RouteFailure(flat.EdgeIndex, failure.Kind, [], [other.EdgeIndex])));
        }

        return new GraphRouteCheck(flat, failures, crossings);
    }

    /// <summary>Flattens a route for collision tests; null when its geometry cannot be trusted.</summary>
    /// <param name="route">The route.</param>
    /// <returns>The flattened route.</returns>
    public GraphFlatRoute? Flatten(EdgeRoute route) => Prepare(route, []);

    /// <summary>Flattens a route; returns null and records the failure when its geometry cannot be trusted.</summary>
    private static GraphFlatRoute? Prepare(EdgeRoute route, List<RouteFailure> failures)
    {
        if (route.Points.Count < 2 || route.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            failures.Add(new RouteFailure(route.EdgeIndex, ERouteFailureKind.NonFiniteCoordinate, [], []));
            return null;
        }

        if (route.Geometry == EEdgeGeometry.Straight)
        {
            return GraphFlatRoute.Of(route.EdgeIndex, route.Points, isChord: route.Points.Count == 2);
        }

        var flat = GraphGeometry.Flatten(route.Points);
        if (!flat.IsComplete)
        {
            failures.Add(new RouteFailure(route.EdgeIndex, ERouteFailureKind.GeometryPrecisionLimit, [], []));
            return null;
        }

        return GraphFlatRoute.Of(route.EdgeIndex, flat.Points);
    }

    /// <summary>
    /// A chord's two ends must sit on the borders of the cards they join: the segment leans however the cards
    /// stand, but a relation that begins in the open is not attached to anything.
    /// </summary>
    private static void CheckEnds(GraphFlatRoute flat, NodePlacement start, NodePlacement end, List<RouteFailure> failures)
    {
        if (DistanceToBorder(start, flat.Points[0]) > BorderTolerance || DistanceToBorder(end, flat.Points[^1]) > BorderTolerance)
        {
            failures.Add(new RouteFailure(flat.EdgeIndex, ERouteFailureKind.InvalidPortApproach, [], []));
        }
    }

    /// <summary>How far a point stands from the nearest side of a card.</summary>
    private static double DistanceToBorder(NodePlacement card, GraphPoint point) =>
        Math.Min(
            Math.Min(Math.Abs(point.X - card.TopLeft.X), Math.Abs(point.X - card.Right)),
            Math.Min(Math.Abs(point.Y - card.TopLeft.Y), Math.Abs(point.Y - card.Bottom)));

    /// <summary>The route must leave its start card and enter its end card along the border's outward normal.</summary>
    private static void CheckApproach(EdgeRoute route, GraphFlatRoute flat, NodePlacement start, NodePlacement end, List<RouteFailure> failures)
    {
        var points = route.Points;
        var leaving = Direction(points, 0, 1);
        // Read backward from the end, so it is the direction the route heads away from its end card if run in reverse.
        var arriving = Direction(points, points.Count - 1, -1);

        var valid = leaving is { } out1 && arriving is { } in1
            && LeavesAlongNormal(start, points[0], out1)
            && LeavesAlongNormal(end, points[^1], in1);

        if (!valid)
        {
            failures.Add(new RouteFailure(flat.EdgeIndex, ERouteFailureKind.InvalidPortApproach, [], []));
        }
    }

    /// <summary>The unit direction from the point at <paramref name="from"/> to the first distinct point stepping by <paramref name="step"/>.</summary>
    private static (double X, double Y)? Direction(IReadOnlyList<GraphPoint> points, int from, int step)
    {
        for (var at = from + step; at >= 0 && at < points.Count; at += step)
        {
            var (dx, dy) = (points[at].X - points[from].X, points[at].Y - points[from].Y);
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length > 1e-9)
            {
                return (dx / length, dy / length);
            }
        }

        return null;
    }

    /// <summary>Whether a point on a card's border, with a direction, heads away from the card by more than the border angle.</summary>
    private static bool LeavesAlongNormal(NodePlacement card, GraphPoint point, (double X, double Y) direction)
    {
        var sides = new (double Distance, double NormalX, double NormalY)[]
        {
            (Math.Abs(point.X - card.TopLeft.X), -1, 0),
            (Math.Abs(point.X - card.Right), 1, 0),
            (Math.Abs(point.Y - card.TopLeft.Y), 0, -1),
            (Math.Abs(point.Y - card.Bottom), 0, 1),
        };
        var nearest = sides.MinBy(side => side.Distance);

        return nearest.Distance <= BorderTolerance
            && ((nearest.NormalX * direction.X) + (nearest.NormalY * direction.Y)) >= MinimumApproachSine;
    }

    /// <summary>
    /// No flattened segment may enter a card. A card the relation does not join is grown by the card clearance;
    /// a card it does join is shrunk instead, because the port is on its border and the stroke has to start
    /// there — a route that runs over its own card enters deeper than that and is a collision like any other.
    /// Each stretch asks the card index for the cards its own box can reach, so a long route across a large
    /// picture costs the cards beside it instead of every card drawn.
    /// </summary>
    private static void CheckCards(
        GraphFlatRoute flat,
        IReadOnlyList<NodePlacement> nodes,
        GraphEdgeEndpoints own,
        GraphCardIndex cards,
        List<RouteFailure> failures)
    {
        var blocking = new SortedSet<int>();
        var grow = GraphGeometryContract.CardCenterlineClearance;

        for (var at = 1; at < flat.Points.Count; at++)
        {
            var (start, end) = (flat.Points[at - 1], flat.Points[at]);
            var near = cards.Near(
                Math.Min(start.X, end.X) - grow,
                Math.Min(start.Y, end.Y) - grow,
                Math.Max(start.X, end.X) + grow,
                Math.Max(start.Y, end.Y) + grow);

            foreach (var position in near)
            {
                var card = nodes[position];
                if (blocking.Contains(card.NodeIndex))
                {
                    continue;
                }

                var clearance = card.NodeIndex == own.ChildNode || card.NodeIndex == own.ParentNode ? -OwnCardInset : grow;
                if (GraphGeometry.SegmentHitsRect(start, end, card, clearance))
                {
                    blocking.Add(card.NodeIndex);
                }
            }
        }

        if (blocking.Count > 0)
        {
            failures.Add(new RouteFailure(flat.EdgeIndex, ERouteFailureKind.CardCollision, blocking.ToArray(), []));
        }
    }

    /// <summary>A route must not cross itself; only the closed loop shape may meet its own ends.</summary>
    private static void CheckSelfIntersection(GraphFlatRoute flat, EdgeRoute route, List<RouteFailure> failures)
    {
        if (CrossesItself(flat, route.Geometry == EEdgeGeometry.Loop))
        {
            failures.Add(new RouteFailure(flat.EdgeIndex, ERouteFailureKind.SelfIntersection, [], []));
        }
    }

    /// <summary>
    /// Whether two stretches of one flattened route meet away from a shared point. A stretch is a neighbour
    /// of the one before and the one after it, and of the last one when the route is a closed loop.
    /// </summary>
    private static bool CrossesItself(GraphFlatRoute flat, bool closed)
    {
        var points = flat.Points;
        for (var at = 1; at < points.Count; at++)
        {
            var (start, end) = (points[at - 1], points[at]);
            foreach (var other in flat.Index.Near(
                Math.Min(start.X, end.X),
                Math.Min(start.Y, end.Y),
                Math.Max(start.X, end.X),
                Math.Max(start.Y, end.Y)))
            {
                var apart = Math.Abs(other - at);
                var closesTheLoop = closed && apart == points.Count - 2;
                if (apart < 2 || closesTheLoop)
                {
                    continue;
                }

                if (GraphGeometry.TryIntersection(start, end, points[other - 1], points[other], out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks every pair of routes: each route is asked of the index of the routes before it, so only the
    /// pairs whose boxes meet are classified instead of every pair drawn.
    /// </summary>
    private static void CheckPairs(List<GraphFlatRoute> flats, List<RouteFailure> failures, List<GraphCrossing> crossings)
    {
        var index = new GraphRouteIndex();
        foreach (var flat in flats.OrderBy(flat => flat.EdgeIndex))
        {
            foreach (var other in index.Near(flat, GraphGeometryContract.MinimumCenterlineDistance))
            {
                CheckPair(other, flat, failures, crossings);
            }

            index.Push(flat);
        }
    }

    /// <summary>Classifies crossings first, then measures shared runs outside their neighborhoods.</summary>
    private static void CheckPair(GraphFlatRoute low, GraphFlatRoute high, List<RouteFailure> failures, List<GraphCrossing> crossings)
    {
        var found = Crossings(low, high);

        // A pair with a chord in it is compared by its crossings alone: the cards are painted over the chord,
        // so a chord behind a card, beside another chord, beside a self-reference's arc or at an angle to a
        // border is not a defect.
        if (low.IsChord || high.IsChord)
        {
            foreach (var crossing in found)
            {
                crossings.Add(new GraphCrossing(low.EdgeIndex, high.EdgeIndex, crossing.Point, crossing.Angle, true));
            }

            return;
        }

        var shared = HasSharedRun(low, high, found);
        var shallow = found.Any(crossing => crossing.Angle < GraphGeometryContract.MinimumCrossingAngle);

        foreach (var crossing in found)
        {
            crossings.Add(new GraphCrossing(
                low.EdgeIndex,
                high.EdgeIndex,
                crossing.Point,
                crossing.Angle,
                !shared && crossing.Angle >= GraphGeometryContract.MinimumCrossingAngle));
        }

        if (shared)
        {
            failures.Add(new RouteFailure(high.EdgeIndex, ERouteFailureKind.StrokeOverlap, [], [low.EdgeIndex]));
        }
        else if (shallow)
        {
            failures.Add(new RouteFailure(high.EdgeIndex, ERouteFailureKind.ShallowCrossing, [], [low.EdgeIndex]));
        }
    }

    private static List<(GraphPoint Point, double Angle)> Crossings(GraphFlatRoute low, GraphFlatRoute high)
    {
        var found = new List<(GraphPoint Point, double Angle)>();
        for (var a = 1; a < low.Points.Count; a++)
        {
            var (p, q) = (low.Points[a - 1], low.Points[a]);
            foreach (var b in high.Index.Near(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y), Math.Max(p.X, q.X), Math.Max(p.Y, q.Y)))
            {
                if (!GraphGeometry.TryIntersection(p, q, high.Points[b - 1], high.Points[b], out var point)
                    || found.Any(known => GraphGeometry.Distance(known.Point, point) < SamePoint))
                {
                    continue;
                }

                found.Add((point, GraphGeometry.CrossingAngle(p, q, high.Points[b - 1], high.Points[b])));
            }
        }

        return found;
    }
    /// <summary>
    /// Whether the first route stays within the centerline distance of the second over a run of at least the
    /// minimum shared run, leaving out samples inside a crossing's neighborhood. A segment is sampled only
    /// against the second route's segments whose boxes lie within that distance, so two routes that merely
    /// share a bounding box cost almost nothing.
    /// </summary>
    private static bool HasSharedRun(GraphFlatRoute low, GraphFlatRoute high, List<(GraphPoint Point, double Angle)> crossings)
    {
        var walked = 0.0;
        double? runStart = null;

        for (var at = 1; at < low.Points.Count; at++)
        {
            var (start, end) = (low.Points[at - 1], low.Points[at]);
            var length = GraphGeometry.Distance(start, end);
            var near = NearSegments(start, end, high);

            if (near.Count == 0)
            {
                runStart = null;
                walked += length;
                continue;
            }

            var pieces = Math.Max(1, (int)Math.Ceiling(length / SampleStep));
            for (var piece = 0; piece <= pieces; piece++)
            {
                var t = (double)piece / pieces;
                var sample = new GraphPoint(start.X + (t * (end.X - start.X)), start.Y + (t * (end.Y - start.Y)));
                var position = walked + (t * length);

                if (IsClose(sample, high, near, crossings))
                {
                    runStart ??= position;
                    if (position - runStart.Value >= GraphGeometryContract.MinimumSharedRun)
                    {
                        return true;
                    }
                }
                else
                {
                    runStart = null;
                }
            }

            walked += length;
        }

        return false;
    }

    /// <summary>The indexes of the other route's segments whose box meets this segment's box grown by the centerline distance.</summary>
    private static List<int> NearSegments(GraphPoint start, GraphPoint end, GraphFlatRoute other)
    {
        var margin = GraphGeometryContract.MinimumCenterlineDistance;

        return other.Index.Near(
            Math.Min(start.X, end.X) - margin,
            Math.Min(start.Y, end.Y) - margin,
            Math.Max(start.X, end.X) + margin,
            Math.Max(start.Y, end.Y) + margin);
    }
    private static bool IsClose(GraphPoint sample, GraphFlatRoute other, List<int> near, List<(GraphPoint Point, double Angle)> crossings)
    {
        if (crossings.Any(crossing => GraphGeometry.Distance(crossing.Point, sample) <= GraphGeometryContract.CrossingNeighborhoodLength))
        {
            return false;
        }

        return near.Any(at => GraphGeometry.DistanceToSegment(sample, other.Points[at - 1], other.Points[at]) < GraphGeometryContract.MinimumCenterlineDistance);
    }
}