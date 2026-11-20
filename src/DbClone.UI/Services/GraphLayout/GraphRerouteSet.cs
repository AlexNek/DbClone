namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Which relations a change to the placement can have invalidated. §9 allows a repair to stay local:
/// only the routes touching the cards it moved may be re-routed. A relation is therefore re-routed when
/// one of its ends sits on a different point of its card — which a move, a widening or the component's
/// repositioning always does — or when a card that moved now covers its box. Every other route is kept
/// exactly as it was.
/// Packing translates a component as a whole, so the previous pass is compared in the frame the new
/// placement is drawn in: the whole-component shift is removed from it first.
/// </summary>
internal static class GraphRerouteSet
{
    private const double Tolerance = 0.01;

    /// <summary>The relations a re-route must include.</summary>
    /// <param name="previous">The evaluation the move started from.</param>
    /// <param name="after">The cards of the placement after the move, as they will be drawn.</param>
    /// <param name="state">The placement state after the move.</param>
    /// <param name="ports">The ports assigned to the placement after the move.</param>
    /// <param name="profile">The graph facts.</param>
    /// <returns>The relation indexes, ascending.</returns>
    public static int[] Of(
        GraphRouteWarmup previous,
        IReadOnlyList<NodePlacement> after,
        GraphPlacementState state,
        GraphPortAssignment ports,
        GraphLayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(profile);

        var moved = Moved(previous.Before, state);
        var (shiftX, shiftY) = Shift(previous, after, state);
        var again = new SortedSet<int>();

        for (var edge = 0; edge < profile.Edges.Count; edge++)
        {
            var (child, parent) = profile.Edges[edge];
            var route = previous.Routes[edge];
            if (route is null
                || !SamePoint(previous.Ends[edge]?.Child, ports.Ends[edge]?.Child, shiftX[child], shiftY[child])
                || !SamePoint(previous.Ends[edge]?.Parent, ports.Ends[edge]?.Parent, shiftX[parent], shiftY[parent])
                || (moved.Count > 0 && Touches(route, moved, after, shiftX[child], shiftY[child])))
            {
                again.Add(edge);
            }
        }

        return again.ToArray();
    }

    /// <summary>How far packing moved each card into the frame the new placement is drawn in.</summary>
    private static (double[] X, double[] Y) Shift(GraphRouteWarmup previous, IReadOnlyList<NodePlacement> after, GraphPlacementState state)
    {
        var x = new double[after.Count];
        var y = new double[after.Count];

        for (var node = 0; node < after.Count; node++)
        {
            x[node] = Local(after[node].TopLeft.X, state.CentreX[node], state.Widths[node])
                - Local(previous.Nodes[node].TopLeft.X, previous.Before.CentreX[node], previous.Before.Widths[node]);
            y[node] = Local(after[node].TopLeft.Y, state.CentreY[node], GraphCard.Height)
                - Local(previous.Nodes[node].TopLeft.Y, previous.Before.CentreY[node], GraphCard.Height);
        }

        return (x, y);
    }

    /// <summary>The placement's own origin, which is what packing translates.</summary>
    private static double Local(double topLeft, double centre, double size) => topLeft - (centre - (size / 2));

    /// <summary>The cards whose centre or width the move changed, including the ones a settle shifted.</summary>
    private static SortedSet<int> Moved(GraphPlacementState before, GraphPlacementState after)
    {
        var moved = new SortedSet<int>();
        for (var node = 0; node < after.Widths.Length; node++)
        {
            if (Math.Abs(before.CentreX[node] - after.CentreX[node]) > Tolerance
                || Math.Abs(before.CentreY[node] - after.CentreY[node]) > Tolerance
                || Math.Abs(before.Widths[node] - after.Widths[node]) > Tolerance)
            {
                moved.Add(node);
            }
        }

        return moved;
    }

    /// <summary>
    /// Whether an end still sits on the same point of its component — the previous point carried into the
    /// frame the new placement is drawn in. The component's own translation cancels, a move does not.
    /// </summary>
    private static bool SamePoint(GraphEdgePort? before, GraphEdgePort? after, double shiftX, double shiftY)
    {
        if (before is null || after is null)
        {
            return before is null && after is null;
        }

        return before.Side == after.Side
            && Math.Abs((before.Point.X + shiftX) - after.Point.X) <= Tolerance
            && Math.Abs((before.Point.Y + shiftY) - after.Point.Y) <= Tolerance;
    }

    /// <summary>
    /// Whether a kept route's box, moved with its own component into the new frame, meets a card the move
    /// shifted. It compared clear in the old frame, so only the new position needs testing.
    /// </summary>
    private static bool Touches(
        EdgeRoute route,
        SortedSet<int> moved,
        IReadOnlyList<NodePlacement> after,
        double shiftX,
        double shiftY)
    {
        var (left, top, right, bottom) = Box(route.Points);
        var grow = GraphGeometryContract.CardCenterlineClearance;

        foreach (var node in moved)
        {
            if (Meets(after[node], grow, left + shiftX, top + shiftY, right + shiftX, bottom + shiftY))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Meets(NodePlacement card, double grow, double left, double top, double right, double bottom) =>
        card.TopLeft.X - grow <= right && card.Right + grow >= left
        && card.TopLeft.Y - grow <= bottom && card.Bottom + grow >= top;

    /// <summary>The box of a route read off its own points, which hold a curve as tightly as its control points allow.</summary>
    private static (double Left, double Top, double Right, double Bottom) Box(IReadOnlyList<GraphPoint> points)
    {
        var (left, top, right, bottom) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var point in points)
        {
            (left, top) = (Math.Min(left, point.X), Math.Min(top, point.Y));
            (right, bottom) = (Math.Max(right, point.X), Math.Max(bottom, point.Y));
        }

        return (left, top, right, bottom);
    }
}
