namespace DbClone.UI.Services.GraphLayout;

/// <summary>Places a shared hub in the middle and its separate communities around it, independent of FK direction.</summary>
internal static class GraphClusterStarLayout
{
    /// <summary>A hub must reach at least three other communities; a chain retains its ranked arrangement.</summary>
    internal static int? FindHub(GraphLayoutProfile profile, GraphClusterSet clusters)
    {
        var connected = Enumerable.Range(0, profile.NodeCount)
            .Select(node => new
            {
                Node = node,
                Communities = profile.Neighbours[node].Select(peer => clusters.ClusterOf[peer])
                    .Where(group => group != clusters.ClusterOf[node]).Distinct().Count()
            })
            .Where(item => item.Communities >= 3)
            .OrderByDescending(item => profile.Neighbours[item.Node].Count)
            .ThenByDescending(item => item.Communities).ThenBy(item => item.Node)
            .FirstOrDefault();
        return connected?.Node;
    }

    /// <summary>Returns the detected hub and partition of a single hub-shaped component.</summary>
    internal static (int Hub, GraphClusterSet Clusters)? Detect(GraphLayoutProfile profile)
    {
        if (profile.ComponentNodes.Count != 1 || profile.ClusterGroups.Count < 4)
            return null;
        var labels = new int[profile.NodeCount];
        for (var group = 0; group < profile.ClusterGroups.Count; group++)
            foreach (var node in profile.ClusterGroups[group]) labels[node] = group;
        var clusters = new GraphClusterSet(labels, profile.ClusterGroups, 0);
        return FindHub(profile, clusters) is { } hub ? (hub, clusters) : null;
    }

    /// <summary>Fits community boxes onto a ring about the hub, with a full gap and no drawn boundaries.</summary>
    internal static IReadOnlyList<GraphPoint> Arrange(
        GraphLayoutProfile profile, GraphClusterSet clusters, IReadOnlyList<GraphLocalLayout> locals, int hub)
    {
        var centre = clusters.ClusterOf[hub];
        var order = SatelliteOrder(profile, clusters, centre);
        var hubAt = locals[centre].Nodes.ToList().IndexOf(hub);
        var hubLocal = new GraphPoint(locals[centre].TopLefts[hubAt].X + profile.Widths[hub] / 2,
            locals[centre].TopLefts[hubAt].Y + GraphCard.Height / 2);
        var radii = locals.Select(local => Math.Sqrt(local.Width * local.Width + local.Height * local.Height) / 2).ToArray();
        var centreRadius = radii[centre] + GraphGeometry.Distance(hubLocal,
            new GraphPoint(locals[centre].Width / 2, locals[centre].Height / 2));
        var radius = centreRadius + order.Max(group => radii[group]) + GraphPlacementConstants.ClusterGap;
        var angles = Enumerable.Range(0, order.Count).Select(at => -Math.PI / 2 + 2 * Math.PI * at / order.Count).ToArray();
        for (var first = 0; first < order.Count; first++)
            for (var second = first + 1; second < order.Count; second++)
            {
                var chord = 2 * Math.Abs(Math.Sin((angles[second] - angles[first]) / 2));
                radius = Math.Max(radius, (radii[order[first]] + radii[order[second]] + GraphPlacementConstants.ClusterGap) / chord);
            }

        var offsets = new GraphPoint[locals.Count];
        offsets[centre] = new GraphPoint(-hubLocal.X, -hubLocal.Y);
        for (var at = 0; at < order.Count; at++)
        {
            var group = order[at];
            offsets[group] = new GraphPoint(radius * Math.Cos(angles[at]) - locals[group].Width / 2,
                radius * Math.Sin(angles[at]) - locals[group].Height / 2);
        }

        var positions = new GraphPoint[profile.NodeCount];
        for (var group = 0; group < locals.Count; group++)
            for (var at = 0; at < locals[group].Nodes.Count; at++)
            {
                var node = locals[group].Nodes[at];
                positions[node] = new GraphPoint(offsets[group].X + locals[group].TopLefts[at].X,
                    offsets[group].Y + locals[group].TopLefts[at].Y);
            }
        return positions;
    }

    /// <summary>Keeps connected satellite communities adjacent around the ring, with stable topology-only ordering.</summary>
    private static IReadOnlyList<int> SatelliteOrder(GraphLayoutProfile profile, GraphClusterSet clusters, int centre)
    {
        var count = clusters.Members.Count;
        var links = new int[count, count];
        foreach (var edge in profile.Edges)
        {
            var from = clusters.ClusterOf[edge.Child];
            var to = clusters.ClusterOf[edge.Parent];
            if (from == to) continue;
            links[from, to]++;
            links[to, from]++;
        }
        var remaining = Enumerable.Range(0, count).Where(group => group != centre).ToList();
        var order = new List<int>();
        while (remaining.Count > 0)
        {
            var last = order.Count == 0 ? centre : order[^1];
            var next = remaining.OrderByDescending(group => links[last, group])
                .ThenByDescending(group => links[centre, group]).ThenBy(group => group).First();
            order.Add(next);
            remaining.Remove(next);
        }
        return order;
    }

    /// <summary>Refinement may shorten spokes but cannot move the hub or gather all satellites below it.</summary>
    internal static bool Preserves(
        IReadOnlyList<NodePlacement> origin, IReadOnlyList<NodePlacement> trial, GraphClusterSet clusters, int hub)
    {
        // Packing/settling translates every card. Remove that translation before checking the pinned hub.
        var dx = origin.Select((card, node) => trial[node].Centre.X - card.Centre.X).Order().ToArray();
        var dy = origin.Select((card, node) => trial[node].Centre.Y - card.Centre.Y).Order().ToArray();
        var shift = new GraphPoint(dx[dx.Length / 2], dy[dy.Length / 2]);
        if (Math.Abs(trial[hub].Centre.X - origin[hub].Centre.X - shift.X) > 1e-6
            || Math.Abs(trial[hub].Centre.Y - origin[hub].Centre.Y - shift.Y) > 1e-6)
            return false;
        foreach (var members in clusters.Members.Where(group => !group.Contains(hub)))
        {
            var before = new GraphPoint(members.Average(node => origin[node].Centre.X) - origin[hub].Centre.X,
                members.Average(node => origin[node].Centre.Y) - origin[hub].Centre.Y);
            var after = new GraphPoint(members.Average(node => trial[node].Centre.X) - trial[hub].Centre.X,
                members.Average(node => trial[node].Centre.Y) - trial[hub].Centre.Y);
            var product = Math.Sqrt((before.X * before.X + before.Y * before.Y) * (after.X * after.X + after.Y * after.Y));
            if (product < 1e-6 || (before.X * after.X + before.Y * after.Y) < Math.Cos(Math.PI / 6) * product)
                return false;
        }
        // The central family's cards also keep a spread around the hub rather than collecting in one rank.
        foreach (var node in clusters.Members[clusters.ClusterOf[hub]].Where(node => node != hub))
        {
            var before = new GraphPoint(origin[node].Centre.X - origin[hub].Centre.X, origin[node].Centre.Y - origin[hub].Centre.Y);
            var after = new GraphPoint(trial[node].Centre.X - trial[hub].Centre.X, trial[node].Centre.Y - trial[hub].Centre.Y);
            var product = Math.Sqrt((before.X * before.X + before.Y * before.Y) * (after.X * after.X + after.Y * after.Y));
            if (product > 1e-6 && (before.X * after.X + before.Y * after.Y) < Math.Cos(Math.PI / 3) * product)
                return false;
        }
        return true;
    }
}
