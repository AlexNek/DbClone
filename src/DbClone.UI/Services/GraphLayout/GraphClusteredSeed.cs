namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The sixth draft seed: detects communities inside each connected component, lays out each cluster with
/// a shared-hub star when one table connects several communities, otherwise ranked boxes and bands.
/// Moves groups and adjusts cards to shorten and clear the border-to-border relations.
/// Finally packs the component boxes with <see cref="GraphComponentPacker"/>.
/// The seed returns null when no component clears the clustering guards.
/// </summary>
internal static class GraphClusteredSeed
{
    /// <summary>Places one graph as a clustered draft.</summary>
    /// <param name="profile">The facts of the graph.</param>
    /// <param name="cancellation">Ends the placement as fast as the engine's own checks allow.</param>
    /// <param name="budget">The evaluation budget the clusters share from the request's total.</param>
    /// <param name="timeLeft">Reports how much time the request still has; a null value reports no time limit.</param>
    /// <returns>The draft named "clustered", or null when no component is admitted for clustering.</returns>
    public static GraphDraftLayout? Place(
        GraphLayoutProfile profile,
        CancellationToken cancellation,
        GraphEvaluationBudget budget,
        Func<TimeSpan>? timeLeft)
    {
        var locals = new List<GraphLocalLayout>();
        var any = false;

        for (var component = 0; component < profile.ComponentNodes.Count; component++)
        {
            cancellation.ThrowIfCancellationRequested();
            var nodes = profile.ComponentNodes[component];
            if (nodes.Count < GraphSearchBudget.MinimumClusteredComponent)
            {
                locals.Add(RankedLocal(profile, component));
                continue;
            }

            var subgraph = profile.Subgraph(component);
            var localIndexes = Enumerable.Range(0, nodes.Count).ToArray();
            var localEdges = Enumerable.Range(0, subgraph.Edges.Count).ToArray();
            var clustering = GraphClusterDetector.Detect(subgraph.Profile, localIndexes, localEdges);
            if (clustering is null)
            {
                locals.Add(RankedLocal(profile, component));
                continue;
            }

            var clustered = TryClusteredLocal(subgraph, clustering, cancellation, timeLeft);
            if (clustered is not null)
            {
                any = true;
                locals.Add(clustered);
                continue;
            }

            locals.Add(RankedLocal(profile, component));
        }

        if (!any)
        {
            return null;
        }

        return GraphComponentPacker.Pack("clustered", profile, locals);
    }

    /// <summary>Produces the ranked placement for one component as a local layout.</summary>
    private static GraphLocalLayout RankedLocal(GraphLayoutProfile profile, int component)
    {
        var subgraph = profile.Subgraph(component);
        var draft = GraphRankedSeed.Place(subgraph.Profile);
        var nodes = profile.ComponentNodes[component];

        var minX = draft.Nodes.Min(p => p.TopLeft.X);
        var minY = draft.Nodes.Min(p => p.TopLeft.Y);
        var positions = draft.Nodes.Select(p => new GraphPoint(p.TopLeft.X - minX, p.TopLeft.Y - minY)).ToArray();
        var width = draft.Nodes.Max(p => p.TopLeft.X + p.Width) - minX;
        var height = draft.Nodes.Max(p => p.TopLeft.Y + p.Height) - minY;

        return new GraphLocalLayout(nodes, positions, width, height);
    }

    /// <summary>
    /// Lays hub-shaped communities around their shared hub, freeing leaves from rank bands.
    /// Components without a shared hub retain their ranked community arrangement.
    /// </summary>
    /// <param name="subgraph">The component's subgraph.</param>
    /// <param name="clustering">The detected clusters of the component.</param>
    /// <param name="cancellation">Checked during group and card placement.</param>
    /// <param name="timeLeft">The enclosing request's remaining watchdog time.</param>
    private static GraphLocalLayout? TryClusteredLocal(
        GraphSubgraph subgraph, GraphClusterSet clustering, CancellationToken cancellation, Func<TimeSpan>? timeLeft)
    {
        var clusterCount = clustering.Members.Count;
        var clusterLocals = new GraphLocalLayout[clusterCount];
        var hub = GraphClusterStarLayout.FindHub(subgraph.Profile, clustering);

        for (var cluster = 0; cluster < clusterCount; cluster++)
        {
            var members = clustering.Members[cluster];
            var remap = members.Select((node, at) => (node, at)).ToDictionary(pair => pair.node, pair => pair.at);
            var clusterProfile = GraphLayoutProfile.Create(
                members.Select(node => subgraph.Profile.Widths[node]).ToArray(),
                ClusterEdges(subgraph.Profile, members, remap));

            var anchor = hub is { } root && members.Contains(root) ? root : members
                .OrderByDescending(node => subgraph.Profile.Neighbours[node].Count(peer => !remap.ContainsKey(peer)))
                .ThenByDescending(node => subgraph.Profile.Neighbours[node].Count).ThenBy(node => node).First();
            var draft = hub is not null
                ? GraphRadialSeed.Place(clusterProfile, remap[anchor])
                : GraphRankedSeed.Place(clusterProfile);
            var lx = draft.Nodes.Min(p => p.TopLeft.X);
            var ly = draft.Nodes.Min(p => p.TopLeft.Y);
            var positions = draft.Nodes.Select(p => new GraphPoint(p.TopLeft.X - lx, p.TopLeft.Y - ly)).ToArray();
            var w = draft.Nodes.Max(p => p.TopLeft.X + p.Width) - lx;
            var h = draft.Nodes.Max(p => p.TopLeft.Y + p.Height) - ly;

            clusterLocals[cluster] = new GraphLocalLayout(members, positions, w, h);
        }

        if (hub is { } centre)
        {
            var star = GraphClusterStarLayout.Arrange(subgraph.Profile, clustering, clusterLocals, centre);
            var eased = GraphClusterPlacementOptimizer.Improve(subgraph.Profile, clustering, star, cancellation, timeLeft, centre);
            return Normalize(subgraph, eased);
        }

        var (offsets, flips, _, _) = GraphClusterArranger.Arrange(clustering, clusterLocals, subgraph.Profile);

        var totalNodes = subgraph.Nodes.Count;
        var finalPositions = new GraphPoint[totalNodes];

        for (var cluster = 0; cluster < clusterCount; cluster++)
        {
            var members = clustering.Members[cluster];
            var local = clusterLocals[cluster];
            for (var at = 0; at < members.Count; at++)
            {
                var node = members[at];

                // The mirror the arranger chose for this box: flipping the content keeps every distance
                // inside the cluster, so the cluster's own picture is unchanged.
                var x = flips[cluster].Horizontal ? local.Width - (local.TopLefts[at].X + subgraph.Profile.Widths[node]) : local.TopLefts[at].X;
                var y = flips[cluster].Vertical ? local.Height - (local.TopLefts[at].Y + GraphCard.Height) : local.TopLefts[at].Y;

                finalPositions[node] = new GraphPoint(offsets[cluster].X + x, offsets[cluster].Y + y);
            }
        }

        var improved = GraphClusterPlacementOptimizer.Improve(subgraph.Profile, clustering, finalPositions, cancellation, timeLeft);
        return Normalize(subgraph, improved);
    }

    private static GraphLocalLayout Normalize(GraphSubgraph subgraph, IReadOnlyList<GraphPoint> positions)
    {
        var minX = positions.Min(point => point.X);
        var minY = positions.Min(point => point.Y);
        var normalized = positions.Select(point => new GraphPoint(point.X - minX, point.Y - minY)).ToArray();
        var width = normalized.Select((point, node) => point.X + subgraph.Profile.Widths[node]).Max();
        var height = normalized.Max(point => point.Y + GraphCard.Height);
        return new GraphLocalLayout(subgraph.Nodes, normalized, width, height);
    }

    private static (int Child, int Parent)[] ClusterEdges(
        GraphLayoutProfile profile,
        IReadOnlyList<int> members,
        Dictionary<int, int> remap)
    {
        var set = new HashSet<int>(members);
        return profile.Edges
            .Where(edge => set.Contains(edge.Child) && set.Contains(edge.Parent) && edge.Child != edge.Parent)
            .Select(edge => (remap[edge.Child], remap[edge.Parent]))
            .ToArray();
    }
}
