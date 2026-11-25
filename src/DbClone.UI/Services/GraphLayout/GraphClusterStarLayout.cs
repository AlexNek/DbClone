namespace DbClone.UI.Services.GraphLayout;

/// <summary>Places a shared hub in the middle and its separate communities around it, independent of FK direction.</summary>
internal static class GraphClusterStarLayout
{
    /// <summary>A hub must reach at least three other communities; a chain retains its ranked arrangement.</summary>
    internal static int? FindHub(GraphLayoutProfile profile, GraphClusterSet clusters)
    {
        var connected = clusters.Members.SelectMany(group => group)
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
        var hubShift = new GraphPoint(trial[hub].Centre.X - origin[hub].Centre.X, trial[hub].Centre.Y - origin[hub].Centre.Y);
        if ((Math.Abs(hubShift.X) > 1e-6 || Math.Abs(hubShift.Y) > 1e-6)
            && (Math.Abs(hubShift.X - shift.X) > 1e-6 || Math.Abs(hubShift.Y - shift.Y) > 1e-6))
            return false;
        var satellites = clusters.Members.Where(group => !group.Contains(hub)).ToArray();
        if (Surrounds(satellites.Select(group => Centre(origin, group)), origin[hub].Centre)
            && !Surrounds(satellites.Select(group => Centre(trial, group)), trial[hub].Centre))
            return false;
        // Preserve the family's spread as a whole, not each card's initial angle. A leaf may change sectors.
        var family = clusters.Members[clusters.ClusterOf[hub]].Where(node => node != hub).ToArray();
        return !Surrounds(family.Select(node => origin[node].Centre), origin[hub].Centre)
            || Surrounds(family.Select(node => trial[node].Centre), trial[hub].Centre);
    }

    private static GraphPoint Centre(IReadOnlyList<NodePlacement> cards, IReadOnlyList<int> members) =>
        new(members.Average(node => cards[node].Centre.X), members.Average(node => cards[node].Centre.Y));

    private static bool Surrounds(IEnumerable<GraphPoint> points, GraphPoint hub)
    {
        var angles = points.Select(point => Math.Atan2(point.Y - hub.Y, point.X - hub.X)).Order().ToArray();
        if (angles.Length < 3) return false;
        for (var at = 0; at < angles.Length; at++)
        {
            var next = at + 1 == angles.Length ? angles[0] + 2 * Math.PI : angles[at + 1];
            if (next - angles[at] >= Math.PI - 1e-6) return false;
        }
        return true;
    }
}
