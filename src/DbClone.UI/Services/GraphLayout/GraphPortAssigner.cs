namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Deals the continuous border positions of every card to the relation ends that meet it. An end prefers
/// the side that faces its other end; ends on one side are ordered by where the other end stands, then by
/// edge and parallel ordinal, so a fan does not braid and parallel relations keep the same order at both
/// ends. Ports are a <see cref="GraphGeometryContract.MinimumPortStep"/> apart and never coincide. A side
/// that is full passes the end to the nearest side with a free slot; a card with no free slot anywhere
/// is a <see cref="ERouteFailureKind.PortCapacity"/> failure, which only a wider card resolves.
/// </summary>
internal static class GraphPortAssigner
{
    /// <summary>
    /// The sides in the order a tie is settled: the border facing the peer first, then the side a relation that
    /// has to give way can take without leaving toward the band it came from. A table whose lateral borders are
    /// full passes the end downwards, where the picture has room to grow around it, before it passes it upwards
    /// into the band its own parents stand in.
    /// </summary>
    private static readonly EEdgeSide[] Sides = [EEdgeSide.Right, EEdgeSide.Bottom, EEdgeSide.Left, EEdgeSide.Top];

    /// <summary>Assigns ports to every relation end. Never cached: call again after every move.</summary>
    /// <param name="nodes">The cards, by node index.</param>
    /// <param name="edges">The relations as child and parent node indexes, in edge-index order.</param>
    /// <returns>The ports, or the capacity failures.</returns>
    public static GraphPortAssignment Assign(IReadOnlyList<NodePlacement> nodes, IReadOnlyList<(int Child, int Parent)> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var ordinals = ParallelOrdinals(edges);
        var ends = new List<GraphPortEnd>();
        for (var edge = 0; edge < edges.Count; edge++)
        {
            ends.Add(new GraphPortEnd(edge, edges[edge].Child, edges[edge].Parent, true, ordinals[edge]));
            ends.Add(new GraphPortEnd(edge, edges[edge].Parent, edges[edge].Child, false, ordinals[edge]));
        }

        var onSide = new Dictionary<(int Node, EEdgeSide Side), List<GraphPortEnd>>();
        var failures = new List<RouteFailure>();

        foreach (var end in ends.OrderBy(end => end.Node).ThenBy(end => end.Edge).ThenByDescending(end => end.IsChild))
        {
            var side = Choose(nodes, end, onSide);
            if (side is null)
            {
                failures.Add(new RouteFailure(end.Edge, ERouteFailureKind.PortCapacity, [end.Node], []));
                continue;
            }

            if (!onSide.TryGetValue((end.Node, side.Value), out var list))
            {
                onSide[(end.Node, side.Value)] = list = [];
            }

            list.Add(end);
        }

        return new GraphPortAssignment(Ports(nodes, edges.Count, onSide), failures.OrderBy(failure => failure.EdgeIndex).ToArray());
    }

    /// <summary>How many ports a border of the given length holds between its corner insets.</summary>
    /// <param name="length">The side's length.</param>
    /// <returns>The slot count, zero when the side is too short for one.</returns>
    public static int Capacity(double length)
    {
        var (first, last) = Interval(length);

        return last < first ? 0 : 1 + (int)Math.Floor(((last - first) / GraphGeometryContract.MinimumPortStep) + 1e-9);
    }

    /// <summary>The least card width whose border holds the given number of ends.</summary>
    /// <param name="ends">How many relation ends meet the card.</param>
    /// <param name="height">The card height.</param>
    /// <returns>The width, which may be below the card's own minimum.</returns>
    public static double MinimumWidth(int ends, double height)
    {
        var needed = Math.Max(0, ends - (2 * Capacity(height)));
        var perLongSide = Math.Max(1, (needed + 1) / 2);

        return BorderWidth(perLongSide);
    }

    /// <summary>
    /// The least card width whose top or bottom border deals the given number of ends a port step apart, with
    /// one step to spare so every end sits inside the corners instead of on them.
    /// </summary>
    /// <param name="ends">How many relation ends meet one border.</param>
    /// <returns>The width, which may be below the card's own minimum.</returns>
    public static double BorderWidth(int ends)
    {
        var slots = Math.Max(1, ends);

        return (2 * GraphGeometryContract.CardCornerClearance) + GraphGeometryContract.StrokeWidth
            + (slots * GraphGeometryContract.MinimumPortStep);
    }

    private static (double First, double Last) Interval(double length) =>
        (GraphGeometryContract.CardCornerClearance + (GraphGeometryContract.StrokeWidth / 2),
         length - GraphGeometryContract.CardCornerClearance - (GraphGeometryContract.StrokeWidth / 2));

    private static double Length(NodePlacement node, EEdgeSide side) =>
        side is EEdgeSide.Top or EEdgeSide.Bottom ? node.Width : node.Height;

    /// <summary>
    /// The border the peer stands in front of, taking the narrowest gap; then the others by angular deviation
    /// from the facing vector. A border faces the peer when the peer lies beyond it and its stretch overlaps
    /// the peer's, which is what makes a wide card that rules a fan face its children along its whole border
    /// and a child face the fan it stands under — neither of them would be found by comparing centres.
    /// </summary>
    private static EEdgeSide? Choose(
        IReadOnlyList<NodePlacement> nodes,
        GraphPortEnd end,
        Dictionary<(int Node, EEdgeSide Side), List<GraphPortEnd>> onSide)
    {
        var (node, peer) = (nodes[end.Node], nodes[end.Peer]);
        var (vx, vy) = end.Node == end.Peer ? (0.0, -1.0) : (peer.Centre.X - node.Centre.X, peer.Centre.Y - node.Centre.Y);

        var candidates = Sides
            .Select((candidate, order) => (
                Side: candidate,
                Order: order,
                Gap: end.Node == end.Peer ? (candidate == EEdgeSide.Top ? 0 : double.MaxValue) : Gap(node, peer, candidate),
                Deviation: Deviation(candidate, vx, vy)))
            .OrderBy(candidate => candidate.Gap).ThenBy(candidate => candidate.Deviation).ThenBy(candidate => candidate.Order);

        foreach (var candidate in candidates)
        {
            var used = onSide.TryGetValue((end.Node, candidate.Side), out var list) ? list.Count : 0;
            if (used < Capacity(Length(node, candidate.Side)))
            {
                return candidate.Side;
            }
        }

        return null;
    }

    /// <summary>
    /// How far beyond a border the peer stands, or <see cref="double.MaxValue"/> when it does not stand beyond
    /// it at all: the peer's stretch across the border must overlap the border's own, and its gap must be
    /// positive. The smallest such gap is the border that faces the peer.
    /// </summary>
    private static double Gap(NodePlacement node, NodePlacement peer, EEdgeSide side)
    {
        var along = side is EEdgeSide.Top or EEdgeSide.Bottom
            ? (node.TopLeft.X, node.Right)
            : (node.TopLeft.Y, node.Bottom);
        var other = side is EEdgeSide.Top or EEdgeSide.Bottom
            ? (peer.TopLeft.X, peer.Right)
            : (peer.TopLeft.Y, peer.Bottom);
        var across = side switch
        {
            EEdgeSide.Top => node.TopLeft.Y - peer.Bottom,
            EEdgeSide.Bottom => peer.TopLeft.Y - node.Bottom,
            EEdgeSide.Left => node.TopLeft.X - peer.Right,
            _ => peer.TopLeft.X - node.Right,
        };

        return across >= 0 && Math.Min(along.Item2, other.Item2) >= Math.Max(along.Item1, other.Item1) ? across : double.MaxValue;
    }

    /// <summary>The angle between a side's outward normal and the facing vector. A tie in the facing vector is settled by side order.</summary>
    private static double Deviation(EEdgeSide side, double vx, double vy)
    {
        var (nx, ny) = side switch
        {
            EEdgeSide.Top => (0.0, -1.0),
            EEdgeSide.Right => (1.0, 0.0),
            EEdgeSide.Bottom => (0.0, 1.0),
            _ => (-1.0, 0.0),
        };
        var length = Math.Sqrt((vx * vx) + (vy * vy));

        return length == 0 ? 0 : Math.Acos(Math.Clamp(((nx * vx) + (ny * vy)) / length, -1, 1));
    }

    private static IReadOnlyList<GraphEdgeEnds?> Ports(IReadOnlyList<NodePlacement> nodes, int edgeCount, Dictionary<(int Node, EEdgeSide Side), List<GraphPortEnd>> onSide)
    {
        var child = new GraphEdgePort?[edgeCount];
        var parent = new GraphEdgePort?[edgeCount];

        foreach (var ((nodeIndex, side), list) in onSide)
        {
            var node = nodes[nodeIndex];
            var ordered = list.OrderBy(end => Along(nodes, end, side)).ThenBy(end => end.Ordinal)
                .ThenBy(end => end.Edge).ThenByDescending(end => end.IsChild).ToArray();
            var (first, last) = Interval(Length(node, side));
            var positions = Positions(nodes, ordered, side, first, last);
            for (var at = 0; at < ordered.Length; at++)
            {
                var port = new GraphEdgePort(side, PointAt(node, side, positions[at]));
                (ordered[at].IsChild ? child : parent)[ordered[at].Edge] = port;
            }
        }

        return Enumerable.Range(0, edgeCount)
            .Select(edge => child[edge] is { } c && parent[edge] is { } p ? new GraphEdgeEnds(c, p) : null)
            .ToArray();
    }

    private static double Along(IReadOnlyList<NodePlacement> nodes, GraphPortEnd end, EEdgeSide side) =>
        side is EEdgeSide.Top or EEdgeSide.Bottom ? nodes[end.Peer].Centre.X : nodes[end.Peer].Centre.Y;

    /// <summary>
    /// Where on the side each end sits. When every peer of the side stands on that border — the border of a
    /// table that receives a fan inside it — each port is dealt to where its own peer stands, so every
    /// relation of the fan leaves on a lane of its own instead of converging into a band and crossing the
    /// cards between. Otherwise the ports are packed one port step apart and the band is centred, so no head
    /// crowds its neighbour. The band is always at least a port step apart and inside the corner insets, in
    /// the order the peers stand in. The wish list is compared in the page's own coordinates, so a widened
    /// card deals its fans over its whole border and not over the first width of it.
    /// </summary>
    private static double[] Positions(
        IReadOnlyList<NodePlacement> nodes,
        GraphPortEnd[] ordered,
        EEdgeSide side,
        double first,
        double last)
    {
        var step = GraphGeometryContract.MinimumPortStep;
        var node = nodes[ordered[0].Node];
        var origin = side is EEdgeSide.Top or EEdgeSide.Bottom ? node.TopLeft.X : node.TopLeft.Y;
        var (low, high) = (origin + first, origin + last);
        var wanted = new double[ordered.Length];
        var above = true;

        for (var at = 0; at < ordered.Length; at++)
        {
            wanted[at] = Along(nodes, ordered[at], side);
            above &= wanted[at] >= low && wanted[at] <= high;
        }

        if (!above)
        {
            var span = (ordered.Length - 1) * step;
            var start = ((low + high) / 2) - (span / 2);
            for (var at = 0; at < ordered.Length; at++)
            {
                wanted[at] = Math.Clamp(start + (at * step), low, high);
            }
        }

        // Two peers that stand in nearly the same place may ask for the same slot: the band is spread around
        // the wish list from both ends, which keeps every port a step apart and as near its peer as it can be.
        var left = (double[])wanted.Clone();
        for (var at = 1; at < left.Length; at++)
        {
            left[at] = Math.Max(left[at], left[at - 1] + step);
        }

        var right = (double[])wanted.Clone();
        for (var at = right.Length - 2; at >= 0; at--)
        {
            right[at] = Math.Min(right[at], right[at + 1] - step);
        }

        for (var at = 0; at < wanted.Length; at++)
        {
            wanted[at] = Math.Clamp((left[at] + right[at]) / 2, low, high) - origin;
        }

        return wanted;
    }

    private static GraphPoint PointAt(NodePlacement node, EEdgeSide side, double along) => side switch
    {
        EEdgeSide.Top => new GraphPoint(node.TopLeft.X + along, node.TopLeft.Y),
        EEdgeSide.Bottom => new GraphPoint(node.TopLeft.X + along, node.Bottom),
        EEdgeSide.Left => new GraphPoint(node.TopLeft.X, node.TopLeft.Y + along),
        _ => new GraphPoint(node.Right, node.TopLeft.Y + along),
    };

    /// <summary>The position of each edge within the set that joins the same pair of tables, in edge order.</summary>
    private static int[] ParallelOrdinals(IReadOnlyList<(int Child, int Parent)> edges)
    {
        var seen = new Dictionary<(int, int), int>();
        var ordinals = new int[edges.Count];
        for (var edge = 0; edge < edges.Count; edge++)
        {
            var key = (Math.Min(edges[edge].Child, edges[edge].Parent), Math.Max(edges[edge].Child, edges[edge].Parent));
            seen.TryGetValue(key, out var before);
            ordinals[edge] = before;
            seen[key] = before + 1;
        }

        return ordinals;
    }
}
