namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The multi-segment candidate family: a smooth curve threaded beside each card the direct segment would
/// cross. A waypoint stands in the lane beside every blocking card, on the side the policy picks; the
/// curve through the ports, an approach point off each port and the waypoints is a Catmull–Rom spline
/// written as connected cubic Béziers, so every join is smooth (C1, hence G1) and the end tangents run
/// along the ports' outward normals. A curve that meets another card takes that card as a blocker too, a
/// bounded number of times.
/// </summary>
internal static class GraphThreadedCurve
{
    private const int Refinements = 3;
    private const double Approach = 24;

    /// <summary>Builds the threaded curve, or null when it cannot clear the cards within the bounded refinements.</summary>
    /// <param name="start">The child's port.</param>
    /// <param name="end">The parent's port.</param>
    /// <param name="blockers">The cards the direct segment crosses.</param>
    /// <param name="cards">All cards.</param>
    /// <param name="own">The two cards the relation joins, which the curve may touch.</param>
    /// <param name="side">+1 or -1 to put every waypoint on one side of the chord; 0 to pick the nearer side per card.</param>
    /// <param name="lane">How far beyond the card clearance the waypoint stands.</param>
    /// <returns>The <c>1 + 3n</c> points of the curve.</returns>
    public static GraphPoint[]? Build(
        GraphEdgePort start,
        GraphEdgePort end,
        IReadOnlyList<int> blockers,
        IReadOnlyList<NodePlacement> cards,
        (int Child, int Parent) own,
        int side,
        double lane)
    {
        var blocking = new SortedSet<int>(blockers);

        for (var round = 0; round <= Refinements; round++)
        {
            var points = Spline(start, end, Waypoints(start.Point, end.Point, blocking, cards, side, lane));
            var flat = GraphGeometry.Flatten(points);
            if (!flat.IsComplete)
            {
                return null;
            }

            var hit = Hits(flat.Points, cards, own);
            if (hit.Count == 0)
            {
                return points;
            }

            var before = blocking.Count;
            blocking.UnionWith(hit);
            if (blocking.Count == before)
            {
                return null;
            }
        }

        return null;
    }

    private static List<GraphPoint> Waypoints(GraphPoint start, GraphPoint end, SortedSet<int> blocking, IReadOnlyList<NodePlacement> cards, int side, double lane)
    {
        var (dx, dy) = (end.X - start.X, end.Y - start.Y);
        var lengthSquared = Math.Max(1e-9, (dx * dx) + (dy * dy));
        var length = Math.Sqrt(lengthSquared);
        var (nx, ny) = (-dy / length, dx / length);
        var placed = new List<(double Along, GraphPoint Point)>();

        foreach (var index in blocking)
        {
            var card = cards[index];
            var centre = card.Centre;
            var along = (((centre.X - start.X) * dx) + ((centre.Y - start.Y) * dy)) / lengthSquared;
            var offset = ((centre.X - start.X) * nx) + ((centre.Y - start.Y) * ny);
            var extent = (Math.Abs(nx) * card.Width / 2) + (Math.Abs(ny) * card.Height / 2);
            var chosen = side != 0 ? side : offset > 0 ? -1 : 1;
            var reach = extent + GraphGeometryContract.CardCenterlineClearance + lane;
            var point = new GraphPoint(centre.X + (chosen * nx * reach), centre.Y + (chosen * ny * reach));
            placed.Add((Math.Clamp(along, 0.02, 0.98), point));
        }

        return placed.OrderBy(item => item.Along).ThenBy(item => item.Point.X).ThenBy(item => item.Point.Y).Select(item => item.Point).ToList();
    }

    /// <summary>The Catmull–Rom spline through the ports, their approach points and the waypoints, as cubic Béziers.</summary>
    private static GraphPoint[] Spline(GraphEdgePort start, GraphEdgePort end, List<GraphPoint> waypoints)
    {
        var (sx, sy) = (start.OutwardX, start.OutwardY);
        var (ex, ey) = (end.OutwardX, end.OutwardY);
        var through = new List<GraphPoint>
        {
            start.Point,
            new(start.Point.X + (sx * Approach), start.Point.Y + (sy * Approach)),
        };
        through.AddRange(waypoints);
        through.Add(new GraphPoint(end.Point.X + (ex * Approach), end.Point.Y + (ey * Approach)));
        through.Add(end.Point);

        // The phantom points beyond each end make the spline's end tangents run along the ports' normals.
        var before = new GraphPoint(start.Point.X - (sx * Approach), start.Point.Y - (sy * Approach));
        var after = new GraphPoint(end.Point.X - (ex * Approach), end.Point.Y - (ey * Approach));
        GraphPoint At(int index) => index < 0 ? before : index >= through.Count ? after : through[index];

        var points = new List<GraphPoint> { through[0] };
        for (var at = 0; at + 1 < through.Count; at++)
        {
            var (p0, p1, p2, p3) = (At(at - 1), At(at), At(at + 1), At(at + 2));
            points.Add(new GraphPoint(p1.X + ((p2.X - p0.X) / 6), p1.Y + ((p2.Y - p0.Y) / 6)));
            points.Add(new GraphPoint(p2.X - ((p3.X - p1.X) / 6), p2.Y - ((p3.Y - p1.Y) / 6)));
            points.Add(p2);
        }

        return points.ToArray();
    }

    private static List<int> Hits(IReadOnlyList<GraphPoint> flat, IReadOnlyList<NodePlacement> cards, (int Child, int Parent) own)
    {
        var (left, right) = (flat.Min(point => point.X), flat.Max(point => point.X));
        var (top, bottom) = (flat.Min(point => point.Y), flat.Max(point => point.Y));
        var grow = GraphGeometryContract.CardCenterlineClearance;
        var hits = new List<int>();

        foreach (var card in cards)
        {
            if (card.NodeIndex == own.Child || card.NodeIndex == own.Parent
                || card.Right + grow < left || card.TopLeft.X - grow > right || card.Bottom + grow < top || card.TopLeft.Y - grow > bottom)
            {
                continue;
            }

            for (var at = 1; at < flat.Count; at++)
            {
                if (GraphGeometry.SegmentHitsCardEnvelope(flat[at - 1], flat[at], card))
                {
                    hits.Add(card.NodeIndex);
                    break;
                }
            }
        }

        return hits;
    }
}
