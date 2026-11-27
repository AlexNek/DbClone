namespace DbClone.UI.Services.GraphLayout;

/// <summary>Topology-only branch moves and geometric sidesteps for a shared-hub star.</summary>
internal static class GraphStarPlacementMoves
{
    /// <summary>Independent branches of the hub's own community, with the hub removed.</summary>
    internal static IReadOnlyList<IReadOnlyList<int>> Branches(GraphLayoutProfile profile, GraphClusterSet clusters, int hub)
    {
        var remaining = clusters.Members[clusters.ClusterOf[hub]].Where(node => node != hub).ToHashSet();
        var branches = new List<IReadOnlyList<int>>();
        while (remaining.Count > 0)
        {
            var pending = new Queue<int>();
            var members = new List<int>();
            var root = remaining.Min();
            remaining.Remove(root);
            pending.Enqueue(root);
            while (pending.TryDequeue(out var node))
            {
                members.Add(node);
                foreach (var peer in profile.Neighbours[node])
                    if (remaining.Remove(peer)) pending.Enqueue(peer);
            }
            branches.Add(members.Order().ToArray());
        }
        return branches;
    }

    /// <summary>Groups a dispersed central branch into layers outward from its hub attachments.</summary>
    internal static IReadOnlyList<GraphPoint> Compact(GraphLayoutProfile profile, IReadOnlyList<int> members, int hub)
    {
        var map = members.Select((node, at) => (node, at)).ToDictionary(pair => pair.node, pair => pair.at);
        var distance = members.ToDictionary(node => node, _ => int.MaxValue);
        var pending = new Queue<int>();
        foreach (var node in members.Where(node => profile.Neighbours[node].Contains(hub)))
        {
            distance[node] = 0;
            pending.Enqueue(node);
        }
        while (pending.TryDequeue(out var node))
            foreach (var peer in profile.Neighbours[node].Where(map.ContainsKey))
                if (distance[peer] == int.MaxValue)
                {
                    distance[peer] = distance[node] + 1;
                    pending.Enqueue(peer);
                }
        var edges = profile.Edges.Where(edge => edge.Child != edge.Parent && map.ContainsKey(edge.Child) && map.ContainsKey(edge.Parent))
            .Select(edge => distance[edge.Child] >= distance[edge.Parent]
                ? (map[edge.Child], map[edge.Parent]) : (map[edge.Parent], map[edge.Child])).ToArray();
        var local = GraphLayoutProfile.Create(members.Select(node => profile.Widths[node]).ToArray(), edges);
        return GraphRankedSeed.Place(local).Nodes.Select(card => card.Centre).ToArray();
    }

    /// <summary>Moves a branch to other sectors without changing its internal relative positions.</summary>
    internal static IEnumerable<GraphPoint> Orbit(IReadOnlyList<NodePlacement> cards, IReadOnlyList<int> members, int hub)
    {
        var centre = new GraphPoint(members.Average(node => cards[node].Centre.X), members.Average(node => cards[node].Centre.Y));
        var root = cards[hub].Centre;
        var radius = GraphGeometry.Distance(centre, root);
        var angle = Math.Atan2(centre.Y - root.Y, centre.X - root.X);
        foreach (var scale in new[] { 1d, 1.25, 0.75 })
            for (var step = 1; step < 16; step++)
            {
                var facing = angle + step * Math.PI / 8;
                yield return new GraphPoint(root.X + scale * radius * Math.Cos(facing) - centre.X,
                    root.Y + scale * radius * Math.Sin(facing) - centre.Y);
            }
    }

    /// <summary>Moves an endpoint across a crossing or around a foreign card, retaining straight spokes.</summary>
    internal static IEnumerable<GraphPoint> Endpoints(GraphLayoutProfile profile, IReadOnlyList<NodePlacement> cards, int node)
    {
        var ports = GraphPortAssigner.Assign(cards, profile.Edges);
        if (!ports.IsComplete) yield break;
        for (var edge = 0; edge < profile.Edges.Count; edge++)
        {
            var relation = profile.Edges[edge];
            if (relation.Child == relation.Parent || (relation.Child != node && relation.Parent != node)) continue;
            var own = ports.Ends[edge]!;
            var from = relation.Child == node ? own.Child.Point : own.Parent.Point;
            var to = relation.Child == node ? own.Parent.Point : own.Child.Point;
            var length = GraphGeometry.Distance(from, to);
            if (length < 1e-6) continue;
            var nx = -(to.Y - from.Y) / length;
            var ny = (to.X - from.X) / length;
            foreach (var card in cards.Where(card => card.NodeIndex != relation.Child && card.NodeIndex != relation.Parent
                && GraphGeometry.SegmentHitsCardEnvelope(from, to, card)))
            {
                var fraction = ((card.Centre.X - from.X) * (to.X - from.X) + (card.Centre.Y - from.Y) * (to.Y - from.Y)) / (length * length);
                var distance = (card.Centre.X - from.X) * nx + (card.Centre.Y - from.Y) * ny;
                var clearance = Math.Abs(nx) * (card.Width / 2 + GraphGeometryContract.CardCenterlineClearance)
                    + Math.Abs(ny) * (card.Height / 2 + GraphGeometryContract.CardCenterlineClearance) + GraphPlacementConstants.CardGap / 2;
                foreach (var side in new[] { -1, 1 })
                {
                    var shift = (distance + side * clearance) / Math.Max(0.1, 1 - fraction);
                    yield return new GraphPoint(nx * shift, ny * shift);
                }
            }
            for (var other = 0; other < profile.Edges.Count; other++)
            {
                var end = ports.Ends[other]!;
                if (other == edge || !GraphGeometry.SegmentsCross(from, to, end.Child.Point, end.Parent.Point)) continue;
                var span = GraphGeometry.Distance(end.Child.Point, end.Parent.Point);
                if (span < 1e-6) continue;
                var ox = -(end.Parent.Point.Y - end.Child.Point.Y) / span;
                var oy = (end.Parent.Point.X - end.Child.Point.X) / span;
                var offset = (cards[node].Centre.X - end.Child.Point.X) * ox + (cards[node].Centre.Y - end.Child.Point.Y) * oy;
                var clearance = Math.Abs(ox) * cards[node].Width / 2 + Math.Abs(oy) * cards[node].Height / 2 + GraphPlacementConstants.CardGap;
                foreach (var side in new[] { -1, 1 })
                    yield return new GraphPoint(ox * (side * clearance - offset), oy * (side * clearance - offset));
            }
        }
    }

    /// <summary>Moves the obstructing card, even when it is not an endpoint of any crossing.</summary>
    internal static IEnumerable<GraphPoint> Clearances(
        GraphLayoutProfile profile, IReadOnlyList<NodePlacement> cards, int node)
    {
        var ports = GraphPortAssigner.Assign(cards, profile.Edges);
        if (!ports.IsComplete) yield break;
        var card = cards[node];
        for (var edge = 0; edge < profile.Edges.Count; edge++)
        {
            var relation = profile.Edges[edge];
            if (relation.Child == node || relation.Parent == node || relation.Child == relation.Parent) continue;
            var from = ports.Ends[edge]!.Child.Point;
            var to = ports.Ends[edge]!.Parent.Point;
            if (!GraphGeometry.SegmentHitsCardEnvelope(from, to, card)) continue;
            var length = GraphGeometry.Distance(from, to);
            if (length < 1e-6) continue;
            var nx = -(to.Y - from.Y) / length;
            var ny = (to.X - from.X) / length;
            var distance = (card.Centre.X - from.X) * nx + (card.Centre.Y - from.Y) * ny;
            var clearance = Math.Abs(nx) * (card.Width / 2 + GraphGeometryContract.CardCenterlineClearance)
                + Math.Abs(ny) * (card.Height / 2 + GraphGeometryContract.CardCenterlineClearance) + 1;
            foreach (var side in new[] { -1, 1 })
            {
                var shift = side * clearance - distance;
                yield return new GraphPoint(nx * shift, ny * shift);
                yield return new GraphPoint(nx * shift * 2, ny * shift * 2);
            }
        }
    }
}
