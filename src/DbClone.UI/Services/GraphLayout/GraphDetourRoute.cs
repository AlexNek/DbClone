namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The route of last resort: an obstacle-avoiding way between two ports, found on the visibility grid the
/// cards themselves define. A relation that has no clear chord and no bow that clears what stands in its way
/// is not "undrawable" — every diagram editor answers such a relation with a corridor route, and so does
/// this one. The path leaves each port along its normal, runs on lanes that stand outside every card, and is
/// written as a chain of straight cubics, which is the dog-leg the design names for a blocked chord.
/// </summary>
/// <remarks>
/// It is asked for only after the straight chord and every bow of one relation have been refused, so an
/// ordinary catalog never pays for it. The lanes are the card edges pushed out by the clearance the validator
/// enforces; a grid that size is bounded by the cap, and the search is a least-length walk that charges for
/// every turn, so the answer turns as few times as it can.
/// </remarks>
internal static class GraphDetourRoute
{
    /// <summary>The lane offset from a card edge: the centerline clearance the validator enforces, and a pixel of slack.</summary>
    private const double Lane = GraphGeometryContract.CardCenterlineClearance + 2;

    /// <summary>
    /// The clearance a lane is tested with. It is a pixel inside the lane, so a lane that runs along a card
    /// edge is outside the rectangle the hit test inflates rather than exactly on its boundary.
    /// </summary>
    private const double Tested = Lane - 1;

    /// <summary>What one turn costs against the path's length, in pixels: turns are paid for, never avoided at any price.</summary>
    private const double TurnCost = 400;

    /// <summary>
    /// The longest way round that is still worth drawing, as a multiple of the direct chord: past it the
    /// relation is refused so the repair may move a table instead, which is the cheaper picture.
    /// </summary>
    private const double Reach = 4;

    /// <summary>The slack the reach keeps beyond the multiple, so short relations are never refused for being short.</summary>
    private const double ReachFloor = 400;

    /// <summary>The most lane lines one axis may hold; past it the grid is cut to the lines around the two ports.</summary>
    private const int MaximumLanes = 240;

    /// <summary>Builds the detour for one relation.</summary>
    /// <param name="start">The child's port.</param>
    /// <param name="end">The parent's port.</param>
    /// <param name="cards">All cards, the two the relation joins included.</param>
    /// <param name="own">The two cards the relation joins, which the path may touch.</param>
    /// <returns>The <c>1 + 3n</c> points of the detour, or null when the ports have no way between them.</returns>
    public static GraphPoint[]? Build(
        GraphEdgePort start,
        GraphEdgePort end,
        IReadOnlyList<NodePlacement> cards,
        (int Child, int Parent) own)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var obstacles = cards
            .Where(card => card.NodeIndex != own.Child && card.NodeIndex != own.Parent)
            .ToArray();

        // The two stubs leave their ports along the outward normal, which is the tangent the port's border
        // already asks of a curve: a detour starts the way every other route starts.
        var exit = Step(start);
        var enter = Step(end);

        if (Inside(exit, obstacles) || Inside(enter, obstacles))
        {
            return null;
        }

        var columns = Lanes(obstacles, card => (card.TopLeft.X, card.Right), exit.X, enter.X);
        var rows = Lanes(obstacles, card => (card.TopLeft.Y, card.Bottom), exit.Y, enter.Y);

        var path = Walk(columns, rows, exit, enter, obstacles);
        if (path is null || path.Count < 2)
        {
            return null;
        }

        // The way round must be worth drawing. A wall that reaches across the whole page has a way round that
        // costs the picture its meaning, and moving one table is then the better answer — which is what the
        // caller's repair does with the refusal this returns.
        var chord = GraphGeometry.Distance(start.Point, end.Point);
        if (Length(path) > Math.Max(Reach * chord, chord + ReachFloor))
        {
            return null;
        }

        return Cubic([start.Point, .. path, end.Point]);
    }

    /// <summary>The length of the polyline the walk returned, in pixels.</summary>
    private static double Length(IReadOnlyList<GraphPoint> path)
    {
        var length = 0.0;
        for (var at = 1; at < path.Count; at++)
        {
            length += GraphGeometry.Distance(path[at - 1], path[at]);
        }

        return length;
    }

    /// <summary>The point a stub reaches, one lane out from the port along its outward normal.</summary>
    private static GraphPoint Step(GraphEdgePort port) =>
        new(port.Point.X + (port.OutwardX * Lane), port.Point.Y + (port.OutwardY * Lane));

    /// <summary>Finds the least walk on the grid that clears every card.</summary>
    /// <returns>The corner points between the two stubs, or null when no walk exists.</returns>
    private static List<GraphPoint>? Walk(
        double[] columns,
        double[] rows,
        GraphPoint exit,
        GraphPoint enter,
        NodePlacement[] obstacles)
    {
        var (fromX, fromY) = (Nearest(columns, exit.X), Nearest(rows, exit.Y));
        var (toX, toY) = (Nearest(columns, enter.X), Nearest(rows, enter.Y));
        var cells = columns.Length * rows.Length;

        // A state is a cell and the axis the walk arrived on, so a turn can be charged for: 0 across, 1 down.
        var cost = new double[cells * 2];
        Array.Fill(cost, double.PositiveInfinity);
        var cameFrom = new int[cells * 2];
        Array.Fill(cameFrom, -1);
        var queue = new PriorityQueue<int, double>();

        for (var axis = 0; axis < 2; axis++)
        {
            var state = ((fromY * columns.Length) + fromX) * 2 + axis;
            cost[state] = 0;
            queue.Enqueue(state, 0);
        }

        var target = (toY * columns.Length) + toX;
        while (queue.TryDequeue(out var state, out var reached))
        {
            if (reached > cost[state])
            {
                continue;
            }

            var (cell, axis) = (state / 2, state % 2);
            var (x, y) = (cell % columns.Length, cell / columns.Length);
            if (cell == target)
            {
                return Points(state, columns, rows, cameFrom);
            }

            for (var side = -1; side <= 1; side += 2)
            {
                foreach (var next in Next(x, y, side, columns, rows, obstacles))
                {
                    var turn = next.Axis == axis ? 0 : TurnCost;
                    var candidate = reached + next.Length + turn;
                    var nextState = next.Cell * 2 + next.Axis;
                    if (candidate < cost[nextState])
                    {
                        cost[nextState] = candidate;
                        cameFrom[nextState] = state;
                        queue.Enqueue(nextState, candidate);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The neighbours of a cell: the next lane on each side, across and down, when that stretch clears every card.</summary>
    private static IEnumerable<(int Cell, int Axis, double Length)> Next(
        int x,
        int y,
        int side,
        double[] columns,
        double[] rows,
        NodePlacement[] obstacles)
    {
        var next = x + side;
        if (next >= 0 && next < columns.Length && Clear(columns[x], rows[y], columns[next], rows[y], obstacles))
        {
            yield return ((y * columns.Length) + next, 0, Math.Abs(columns[next] - columns[x]));
        }

        var down = y + side;
        if (down >= 0 && down < rows.Length && Clear(columns[x], rows[y], columns[x], rows[down], obstacles))
        {
            yield return ((down * columns.Length) + x, 1, Math.Abs(rows[down] - rows[y]));
        }
    }

    /// <summary>Whether a stretch of one lane clears every card.</summary>
    private static bool Clear(double x0, double y0, double x1, double y1, NodePlacement[] obstacles)
    {
        var (left, right) = (Math.Min(x0, x1), Math.Max(x0, x1));
        var (top, bottom) = (Math.Min(y0, y1), Math.Max(y0, y1));
        var from = new GraphPoint(x0, y0);
        var to = new GraphPoint(x1, y1);

        foreach (var card in obstacles)
        {
            if (card.Right + Tested < left || card.TopLeft.X - Tested > right
                || card.Bottom + Tested < top || card.TopLeft.Y - Tested > bottom)
            {
                continue;
            }

            if (GraphGeometry.SegmentHitsRect(from, to, card, Tested))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a point stands inside a card's lane.</summary>
    private static bool Inside(GraphPoint point, NodePlacement[] obstacles) =>
        obstacles.Any(card =>
            point.X > card.TopLeft.X - Tested && point.X < card.Right + Tested
            && point.Y > card.TopLeft.Y - Tested && point.Y < card.Bottom + Tested);

    /// <summary>The lane lines of one axis: every card's two edges pushed out, the two ports, and the ground around them all.</summary>
    private static double[] Lanes(
        NodePlacement[] obstacles,
        Func<NodePlacement, (double Low, double High)> edges,
        double first,
        double second)
    {
        var lines = new List<double> { first, second };
        foreach (var card in obstacles)
        {
            var (low, high) = edges(card);
            lines.Add(low - Lane);
            lines.Add(high + Lane);
        }

        var near = lines.Min();
        var far = lines.Max();
        lines.Add(near - (2 * Lane));
        lines.Add(far + (2 * Lane));

        var sorted = lines.Distinct().Order().ToArray();
        if (sorted.Length <= MaximumLanes)
        {
            return sorted;
        }

        // Past the cap the grid keeps what stands around the two ports and the outer ground: a shorter walk
        // is wrong, a walk that never ends is worse.
        var kept = sorted
            .Where(line => Math.Abs(line - first) < 1e-9 || Math.Abs(line - second) < 1e-9 || line < near + (2 * Lane) || line > far - (2 * Lane))
            .Take(MaximumLanes)
            .ToList();

        return kept.Distinct().Order().ToArray();
    }

    /// <summary>The lane nearest a coordinate, which is a port's own line whenever the grid holds it.</summary>
    private static int Nearest(double[] lanes, double value)
    {
        var at = Array.BinarySearch(lanes, value);
        if (at >= 0)
        {
            return at;
        }

        var after = ~at;
        var before = after - 1;
        return before < 0 ? 0
            : after >= lanes.Length ? lanes.Length - 1
            : Math.Abs(lanes[before] - value) <= Math.Abs(lanes[after] - value) ? before : after;
    }

    /// <summary>Reads the corners back out of the walk, dropping the points a straight run makes redundant.</summary>
    private static List<GraphPoint> Points(int state, double[] columns, double[] rows, int[] cameFrom)
    {
        var points = new List<GraphPoint>();
        for (var at = state; at >= 0; at = cameFrom[at])
        {
            var cell = at / 2;
            points.Add(new GraphPoint(columns[cell % columns.Length], rows[cell / columns.Length]));
        }

        points.Reverse();

        var corners = new List<GraphPoint> { points[0] };
        for (var at = 1; at + 1 < points.Count; at++)
        {
            var (before, here, after) = (points[at - 1], points[at], points[at + 1]);
            var straight = (Math.Abs(before.X - here.X) < 1e-9 && Math.Abs(here.X - after.X) < 1e-9)
                || (Math.Abs(before.Y - here.Y) < 1e-9 && Math.Abs(here.Y - after.Y) < 1e-9);
            if (!straight)
            {
                corners.Add(here);
            }
        }

        corners.Add(points[^1]);

        return corners;
    }

    /// <summary>Writes a polyline as the <c>1 + 3n</c> points of connected cubics, each stretch straight between its corners.</summary>
    private static GraphPoint[] Cubic(IReadOnlyList<GraphPoint> path)
    {
        var points = new List<GraphPoint> { path[0] };
        for (var at = 0; at + 1 < path.Count; at++)
        {
            var (from, to) = (path[at], path[at + 1]);
            var (dx, dy) = ((to.X - from.X) / 3, (to.Y - from.Y) / 3);
            points.Add(new GraphPoint(from.X + dx, from.Y + dy));
            points.Add(new GraphPoint(from.X + (2 * dx), from.Y + (2 * dy)));
            points.Add(to);
        }

        return points.ToArray();
    }
}
