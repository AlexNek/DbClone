using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Draws the arrows of a picture arranged in rings: each constraint is drawn the shortest way between
/// the two border points its ends were given, and bowed aside only when that way is not free.
/// </summary>
/// <remarks>
/// <para>
/// A lattice routes in corridors, because a card standing between two tables leaves a straight line
/// nowhere to go. Rings leave that space empty on purpose, so the arrow a reader follows is the chord
/// between two tables and not a path that turns twice past them — and the two rules the picture is
/// held to still decide which chord may be drawn: none may cross a card it does not join, and no two
/// may be drawn along one line. Both are read on the ink of the page, by the same
/// <see cref="TrackUse"/> the corridor router consults, so a bowed chord and a straight one cannot be
/// laid on one another any more than two lanes can.
/// </para>
/// <para>
/// When the straight way is taken, the chord is bowed a little further to one side and tried again,
/// then a little further, on alternating sides: the least bow that clears is the one drawn, so the
/// picture keeps straight arrows wherever a straight arrow is possible and spends a curve only where
/// two arrows were about to become one. The same search that clears a card now clears an arrow too — a
/// way across the open is not free while another arrow is drawn over it — because an arrow the reader
/// cannot follow to its table is the complaint the picture was drawn to answer. A constraint that
/// cannot be cleared at all takes the fewest crossings it can be given, which is what the corridor
/// router does for the same shape (T26).
/// </para>
/// <para>
/// An arrow's ends are dealt by <see cref="GraphEdgePorts"/> like any other picture's. Left to fall
/// where the line from centre to centre crosses the border, the fan of tables one hub names would print
/// every head on one point of it — the case the picture's first rule forbids, and the reason a hub is
/// drawn with its arrows leaving one after another along its border instead of all at once.
/// </para>
/// <para>
/// What an arrow means is stated whatever arrangement drew it: a constraint whose two ends sit in one
/// cyclic group is a <see cref="EEdgeShape.Detour">Detour</see> here as much as in a lattice, even when
/// rings carry it on a straight chord, and a chord bowed aside to dodge a card is a plain
/// <see cref="EEdgeShape.Line">Line</see>, even though it is drawn from four points. The two facts are
/// told apart by <see cref="EdgeRoute.Geometry"/>, because the shape cannot say whether four points are
/// a curve's controls or the corners of a turn, and reading it as the wrong one draws a diagonal the
/// router never routed.
/// </para>
/// </remarks>
internal static class GraphChordRouter
{
    /// <summary>How far a chord is bowed aside in one try.</summary>
    internal const double BowStep = 26;

    /// <summary>How many bows are tried on each side before the least bad way is taken.</summary>
    internal const int BowTries = 10;

    /// <summary>
    /// How far from its card's border a sample still belongs to the arrow rather than to the table it
    /// leaves: an end is dealt a point on the border, and the first steps off it are allowed to graze
    /// the card that carries them.
    /// </summary>
    private const double PortRelief = 14;

    /// <summary>How finely one straight stretch is cut for the crossing tests.</summary>
    private const int SegmentSteps = 8;

    /// <summary>Routes every constraint of a picture placed in rings.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="placement">Where every card sits.</param>
    /// <returns>One route per edge, in edge-index order.</returns>
    public static IReadOnlyList<EdgeRoute> Route(
        CatalogGraphModel model,
        IReadOnlyList<NodePlacement> placement)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(placement);

        var groupOfNode = GraphEdgeRouter.GroupOfNode(model);
        var ends = GraphEdgePorts.Assign(model, placement);
        var tracks = new TrackUse();
        var routes = new EdgeRoute[model.Edges.Count];
        var drawn = new List<Drawn>();

        foreach (var edge in Order(model, placement))
        {
            var route = edge.IsSelfReference
                ? Loop(edge, placement[edge.ChildIndex], ends[edge.Index], tracks)
                : Chord(edge, placement, ends[edge.Index], tracks, ClosesCycle(groupOfNode, edge), drawn);

            tracks.Claim(route);
            routes[edge.Index] = route;
            var trace = Trace(route);
            drawn.Add(new Drawn(edge, trace, Bounds(trace)));
        }

        return routes;
    }

    /// <summary>One arrow already on the page: what it joins, the stretches it covers, and their box.</summary>
    private sealed record Drawn(CatalogGraphEdge Edge, IReadOnlyList<GraphPoint> Trace, (GraphPoint Low, GraphPoint High) Box);

    /// <summary>
    /// The order the arrows are drawn in: a self-reference first, because it hugs its own card and
    /// claims the least ground, then the short chords, because a chord that has to cross the picture
    /// should be the one that bows aside rather than the one with a clear way to itself.
    /// </summary>
    private static IEnumerable<CatalogGraphEdge> Order(CatalogGraphModel model, IReadOnlyList<NodePlacement> placement) =>
        model.Edges
            .OrderBy(edge => edge.IsSelfReference ? 0 : 1)
            .ThenBy(edge => Distance(
                placement[edge.ChildIndex].Centre, placement[edge.ParentIndex].Centre))
            .ThenBy(edge => edge.Index);

    /// <summary>Whether the constraint is one that closes a cycle: both its ends sit in one group.</summary>
    private static bool ClosesCycle(int[] groupOfNode, CatalogGraphEdge edge) =>
        groupOfNode[edge.ChildIndex] >= 0 && groupOfNode[edge.ChildIndex] == groupOfNode[edge.ParentIndex];

    /// <summary>
    /// One constraint drawn: the straight chord when no card stands in it, no arrow lies along it and no
    /// arrow is drawn across it; the least bow that clears those when it does not; and the narrowest
    /// crossing when the picture leaves no other way round.
    /// </summary>
    private static EdgeRoute Chord(
        CatalogGraphEdge edge,
        IReadOnlyList<NodePlacement> placement,
        GraphEdgeEnds ends,
        TrackUse tracks,
        bool closesCycle,
        IReadOnlyList<Drawn> drawn)
    {
        var start = ends.Child.Point;
        var finish = ends.Parent.Point;
        EdgeRoute? clear = null;
        EdgeRoute? fewest = null;
        var crossings = int.MaxValue;

        for (var step = 0; step <= BowTries; step++)
        {
            foreach (var sign in step == 0 ? new[] { 0 } : new[] { -1, 1 })
            {
                var route = Bowed(edge.Index, start, finish, sign * step * BowStep, closesCycle);
                var hits = CardHits(route, placement, edge);

                if (hits < crossings)
                {
                    crossings = hits;
                    fewest = route;
                }

                if (hits > 0 || !ApproachesOutward(route, ends))
                {
                    continue;
                }

                clear ??= route;

                if (tracks.IsShared(route.Points, route.Geometry))
                {
                    continue;
                }

                if (!IsCutByAnArrow(edge, route, drawn))
                {
                    return route;
                }
            }
        }

        return clear ?? fewest ?? Bowed(edge.Index, start, finish, 0, closesCycle);
    }

    /// <summary>
    /// Whether the route steps off both of its cards on the outside of the sides its ends stand on. A chord
    /// drawn straight into a side the peer does not stand on comes in round the back of its own table — the
    /// end was handed that side because the side it faces was full — and the reader cannot tell which table
    /// the arrow belongs to. Swinging the bow is what turns the end aside, so the router bows before it
    /// draws that chord.
    /// </summary>
    private static bool ApproachesOutward(EdgeRoute route, GraphEdgeEnds ends)
    {
        if (route.Geometry != EEdgeGeometry.Polyline)
        {
            return true;
        }

        return StepsOff(route.Points[0], route.Points[1], ends.Child)
            && StepsOff(route.Points[^1], route.Points[^2], ends.Parent);
    }

    private static bool StepsOff(GraphPoint from, GraphPoint to, GraphEdgePort port) =>
        ((to.X - from.X) * port.OutwardX) + ((to.Y - from.Y) * port.OutwardY) > 0;

    /// <summary>
    /// Whether the candidate is drawn across an arrow already on the page. An arrow that joins one of the
    /// same two tables is left out: those meet on a card's border by the picture's own design, and the
    /// reader follows them from the table they name. A route whose box misses an arrow's box is turned
    /// away before its stretches are compared, and the search stops at the first arrow it meets — it is
    /// asked only whether a way is free, and the bows are tried outward from the straight chord, so the
    /// least bow that is free is the one the loop goes on to take.
    /// </summary>
    private static bool IsCutByAnArrow(CatalogGraphEdge edge, EdgeRoute route, IReadOnlyList<Drawn> drawn)
    {
        var trace = Trace(route);
        var (low, high) = Bounds(trace);

        foreach (var held in drawn)
        {
            if (SharesTable(edge, held.Edge) || BoxesApart(low, high, held.Box))
            {
                continue;
            }

            if (AnyStretchCrosses(trace, held.Trace))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SharesTable(CatalogGraphEdge a, CatalogGraphEdge b) =>
        a.ChildIndex == b.ChildIndex || a.ChildIndex == b.ParentIndex
        || a.ParentIndex == b.ChildIndex || a.ParentIndex == b.ParentIndex;

    private static bool BoxesApart(GraphPoint low, GraphPoint high, (GraphPoint Low, GraphPoint High) box) =>
        box.High.X < low.X || box.Low.X > high.X || box.High.Y < low.Y || box.Low.Y > high.Y;

    private static bool AnyStretchCrosses(IReadOnlyList<GraphPoint> first, IReadOnlyList<GraphPoint> second)
    {
        for (var a = 0; a + 1 < first.Count; a++)
        {
            for (var b = 0; b + 1 < second.Count; b++)
            {
                if (GraphGeometry.SegmentsCross(first[a], first[a + 1], second[b], second[b + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A table pointing at itself: the loop bowed out of the two points its ends were given on the one
    /// card, so two self-references are drawn as two arrows. A reach that lands on an arrow already
    /// drawn gives way to a shorter one inside the same space.
    /// </summary>
    private static EdgeRoute Loop(
        CatalogGraphEdge edge,
        NodePlacement node,
        GraphEdgeEnds ends,
        TrackUse tracks)
    {
        EdgeRoute? accepted = null;

        foreach (var reach in new[] { GraphEdgeRouter.LoopReach, 20, 34 })
        {
            var points = new[]
            {
                ends.Child.Point,
                Outward(ends.Child, reach),
                Outward(ends.Parent, reach),
                ends.Parent.Point,
            };
            var route = new EdgeRoute(edge.Index, EEdgeShape.Loop, EEdgeGeometry.Cubic, points,
                new GraphPoint(node.Right + reach + 8, node.Centre.Y));
            accepted ??= route;

            if (!tracks.IsShared(points, EEdgeGeometry.Cubic))
            {
                return route;
            }
        }

        return accepted!;
    }

    /// <summary>
    /// A chord bowed aside by the given distance: the two ends and the two control points of the cubic
    /// the document paints for a quadratic bow. Zero bows nothing, and the chord stays the two points of
    /// a straight line. Either way the constraint means what it meant before it was bowed — a cycle's
    /// closing edge is a detour drawn straight, and an ordinary one is a line drawn round a card.
    /// </summary>
    private static EdgeRoute Bowed(
        int edgeIndex,
        GraphPoint start,
        GraphPoint finish,
        double offset,
        bool closesCycle)
    {
        var shape = closesCycle ? EEdgeShape.Detour : EEdgeShape.Line;

        if (Math.Abs(offset) < 0.001)
        {
            return new EdgeRoute(edgeIndex, shape, EEdgeGeometry.Polyline, [start, finish], Midpoint(start, finish));
        }

        var (dx, dy) = (finish.X - start.X, finish.Y - start.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var (peakX, peakY) = length < 0.001
            ? ((start.X + finish.X) / 2, (start.Y + finish.Y) / 2)
            : (((start.X + finish.X) / 2) - ((dy / length) * offset), ((start.Y + finish.Y) / 2) + ((dx / length) * offset));

        var first = new GraphPoint(start.X + (0.6667 * (peakX - start.X)), start.Y + (0.6667 * (peakY - start.Y)));
        var second = new GraphPoint(finish.X + (0.6667 * (peakX - finish.X)), finish.Y + (0.6667 * (peakY - finish.Y)));

        return new EdgeRoute(edgeIndex, shape, EEdgeGeometry.Cubic, [start, first, second, finish],
            new GraphPoint(
                (start.X + (2 * peakX) + finish.X) / 4,
                (start.Y + (2 * peakY) + finish.Y) / 4));
    }

    /// <summary>
    /// How many cards the route is drawn across. A card the arrow joins is tested from the point its
    /// own end is allowed to leave: an arrow that dives into the table it starts on has been drawn
    /// through that table, while the first steps off its border belong to the arrow and to nothing else.
    /// </summary>
    private static int CardHits(
        EdgeRoute route,
        IReadOnlyList<NodePlacement> placement,
        CatalogGraphEdge edge)
    {
        var trace = Trace(route);
        var (low, high) = Bounds(trace);
        var hits = 0;

        foreach (var card in placement)
        {
            var joins = card.NodeIndex == edge.ChildIndex || card.NodeIndex == edge.ParentIndex;

            if (joins)
            {
                hits += OwnCard(card, trace, edge);
                continue;
            }

            if (card.TopLeft.X > (high.X + GraphEdgeRouter.Clearance)
                || card.Right < (low.X - GraphEdgeRouter.Clearance)
                || card.TopLeft.Y > (high.Y + GraphEdgeRouter.Clearance)
                || card.Bottom < (low.Y - GraphEdgeRouter.Clearance))
            {
                continue;
            }

            for (var index = 0; index + 1 < trace.Count; index++)
            {
                if (GraphGeometry.SegmentHitsRect(trace[index], trace[index + 1], card, GraphEdgeRouter.Clearance))
                {
                    hits++;
                    break;
                }
            }
        }

        return hits;
    }

    /// <summary>Whether a route's own card is drawn through by the part of it that has left its border.</summary>
    private static int OwnCard(
        NodePlacement card,
        IReadOnlyList<GraphPoint> trace,
        CatalogGraphEdge edge)
    {
        var child = trace[0];
        var parent = trace[^1];
        var end = card.NodeIndex == edge.ChildIndex ? child : parent;
        var entered = 0;

        for (var index = 1; index < trace.Count - 1; index++)
        {
            if (Distance(trace[index], end) < PortRelief)
            {
                continue;
            }

            if (GraphGeometry.Contains(card, trace[index]))
            {
                entered++;
            }
        }

        return entered;
    }

    /// <summary>
    /// The route read as the short stretches the document paints for it: a curve is walked between its
    /// control points and a straight chord is cut into steps as well, because the way an arrow dives
    /// through its own table shows itself only between the two ends it was drawn from.
    /// </summary>
    private static IReadOnlyList<GraphPoint> Trace(EdgeRoute route)
    {
        if (route.Geometry != EEdgeGeometry.Polyline)
        {
            var curve = new List<GraphPoint>(GraphEdgeRouter.TraceSamples + 1);
            for (var sample = 0; sample <= GraphEdgeRouter.TraceSamples; sample++)
            {
                curve.Add(GraphEdgeRouter.CubicAt(route.Points, (double)sample / GraphEdgeRouter.TraceSamples));
            }

            return curve;
        }

        var trace = new List<GraphPoint> { route.Points[0] };
        for (var index = 0; index + 1 < route.Points.Count; index++)
        {
            var (from, to) = (route.Points[index], route.Points[index + 1]);
            for (var step = 1; step <= SegmentSteps; step++)
            {
                var share = (double)step / SegmentSteps;
                trace.Add(new GraphPoint(from.X + ((to.X - from.X) * share), from.Y + ((to.Y - from.Y) * share)));
            }
        }

        return trace;
    }

    /// <summary>The box a list of points covers, so a card outside it is never measured.</summary>
    private static (GraphPoint Low, GraphPoint High) Bounds(IReadOnlyList<GraphPoint> trace)
    {
        var (lowX, lowY, highX, highY) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

        foreach (var point in trace)
        {
            lowX = Math.Min(lowX, point.X);
            lowY = Math.Min(lowY, point.Y);
            highX = Math.Max(highX, point.X);
            highY = Math.Max(highY, point.Y);
        }

        return (new GraphPoint(lowX, lowY), new GraphPoint(highX, highY));
    }

    /// <summary>The point a route reaches once it has left an end by the given distance.</summary>
    private static GraphPoint Outward(GraphEdgePort port, double distance) =>
        new(port.Point.X + (port.OutwardX * distance), port.Point.Y + (port.OutwardY * distance));

    private static GraphPoint Midpoint(GraphPoint start, GraphPoint finish) =>
        new((start.X + finish.X) / 2, (start.Y + finish.Y) / 2);

    private static double Distance(GraphPoint from, GraphPoint to) => Math.Sqrt(
        ((to.X - from.X) * (to.X - from.X)) + ((to.Y - from.Y) * (to.Y - from.Y)));
}
