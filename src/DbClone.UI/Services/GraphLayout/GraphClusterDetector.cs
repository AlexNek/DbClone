namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Finds the communities inside one connected component by multi-level modularity optimisation (the Louvain
/// method, Blondel et al. 2008), reported only when the split clears every guard in <see cref="GraphSearchBudget"/>.
/// Edges are undirected and parallel relations merge into one weight; self-references are ignored. A graph the
/// guards reject returns null, so the caller leaves it whole.
/// </summary>
/// <remarks>
/// The method is deterministic: neighbours are visited in ascending index order, the gain threshold is
/// <c>1e-12</c>, and nodes move one at a time to the neighbouring community that maximises modularity.
/// A pass that moves nothing terminates. Levels repeat — fast local moves, then aggregate every community
/// into one node and move again — until a level merges nothing, as D1 specifies. Aggregation preserves the
/// total edge weight, so every level is judged against the original 2m and modularity is measured on the
/// original graph from the composed partition.
/// </remarks>
internal static class GraphClusterDetector
{
    private const double GainThreshold = 1e-12;

    /// <summary>Detects the clusters in one connected component.</summary>
    /// <param name="profile">The component's profile.</param>
    /// <param name="nodes">The nodes of the component (indexes into the global profile).</param>
    /// <param name="edges">The edge indexes inside the component.</param>
    /// <returns>The clusters, or null when the guards reject the split.</returns>
    public static GraphClusterSet? Detect(GraphLayoutProfile profile, IReadOnlyList<int> nodes, IReadOnlyList<int> edges)
    {
        var count = nodes.Count;
        if (count < GraphSearchBudget.MinimumClusteredComponent)
        {
            return null;
        }

        var adjacency = BuildAdjacency(count, profile, nodes, edges);
        var totalWeight = 0d;
        for (var at = 0; at < count; at++)
        {
            totalWeight += adjacency[at].Sum(pair => pair.Item2);
        }

        if (totalWeight <= 0)
        {
            return null;
        }

        var community = DetectCommunities(adjacency, totalWeight, count);
        var distinct = RelabelClusters(community, count);

        if (distinct <= 1)
        {
            return null;
        }

        var modularity = ComputeModularity(community, adjacency, totalWeight);
        if (modularity < GraphSearchBudget.MinimumClusterModularity)
        {
            return null;
        }

        MergeSmallClusters(community, adjacency, count);
        distinct = RelabelClusters(community, count);

        if (distinct <= 1 || distinct > GraphSearchBudget.MaximumClusteredClusters)
        {
            return null;
        }

        var finalModularity = ComputeModularity(community, adjacency, totalWeight);
        if (finalModularity < GraphSearchBudget.MinimumClusterModularity)
        {
            return null;
        }

        var members = BuildMembers(community, distinct, count);
        return new GraphClusterSet(community, members, finalModularity);
    }

    /// <summary>Builds the undirected, weight-merged adjacency for one component.</summary>
    private static (int Node, double Weight)[][] BuildAdjacency(
        int count,
        GraphLayoutProfile profile,
        IReadOnlyList<int> nodes,
        IReadOnlyList<int> edges)
    {
        var position = new Dictionary<int, int>(count);
        for (var at = 0; at < count; at++)
        {
            position[nodes[at]] = at;
        }

        var lists = new Dictionary<int, double>[count];
        for (var at = 0; at < count; at++)
        {
            lists[at] = [];
        }

        foreach (var edge in edges)
        {
            var (child, parent) = profile.Edges[edge];
            if (child == parent)
            {
                continue;
            }

            if (!position.TryGetValue(child, out var from) || !position.TryGetValue(parent, out var to))
            {
                continue;
            }

            lists[from][to] = lists[from].GetValueOrDefault(to) + 1;
            lists[to][from] = lists[to].GetValueOrDefault(from) + 1;
        }

        var result = new (int, double)[count][];
        for (var at = 0; at < count; at++)
        {
            result[at] = lists[at].OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToArray();
        }

        return result;
    }

    /// <summary>
    /// Runs the Louvain levels until a level merges nothing. Each level applies fast local moves, then
    /// aggregates every community into a single node (its internal edges becoming a self-loop that counts
    /// twice in the node's degree) and moves again. <c>membership[node]</c> composes the per-level labels
    /// back onto the original nodes, so the returned partition is over the component as it was given.
    /// </summary>
    private static int[] DetectCommunities((int, double)[][] adjacency, double totalWeight, int count)
    {
        var membership = Enumerable.Range(0, count).ToArray();
        var current = adjacency;
        var currentCount = count;

        while (true)
        {
            var community = FastLocalMoves(current, totalWeight, currentCount);
            var distinct = RelabelClusters(community, currentCount);

            // A level that leaves every node in its own singleton merges nothing and cannot be aggregated
            // finer, so it is the last level; the composed membership is already the final partition.
            if (distinct >= currentCount)
            {
                return membership;
            }

            for (var node = 0; node < count; node++)
            {
                membership[node] = community[membership[node]];
            }

            current = Aggregate(current, community, distinct);
            currentCount = distinct;
        }
    }

    /// <summary>
    /// Aggregates one level into one node per community. Every edge is re-added between the communities it
    /// joins; edges inside one community become that node's self-loop, and because the source adjacency is
    /// symmetric each internal edge enters the loop twice — which is what keeps a node's degree, and so the
    /// fast-local-moves gain formula, correct across levels.
    /// </summary>
    private static (int, double)[][] Aggregate((int, double)[][] current, int[] community, int distinct)
    {
        var lists = new Dictionary<int, double>[distinct];
        for (var at = 0; at < distinct; at++)
        {
            lists[at] = [];
        }

        for (var node = 0; node < current.Length; node++)
        {
            var from = community[node];
            foreach (var (next, weight) in current[node])
            {
                var to = community[next];
                lists[from][to] = lists[from].GetValueOrDefault(to) + weight;
            }
        }

        var result = new (int, double)[distinct][];
        for (var at = 0; at < distinct; at++)
        {
            result[at] = lists[at].OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToArray();
        }

        return result;
    }

    /// <summary>
    /// Runs the Louvain fast local moves: repeatedly sweep nodes in index order and move each to the
    /// neighbouring community that maximises the modularity gain, until a full sweep changes nothing.
    /// A group is never capped by size here: the communities a schema's relations form are what the reader
    /// sees as its sub-groups, and forcing a large one apart trades a wide box for a crowd of long relations
    /// running between the boxes, which draws more crossings, not fewer.
    /// </summary>
    private static int[] FastLocalMoves((int, double)[][] adjacency, double totalWeight, int count)
    {
        var community = Enumerable.Range(0, count).ToArray();
        var degrees = new double[count];
        for (var node = 0; node < count; node++)
        {
            degrees[node] = adjacency[node].Sum(pair => pair.Item2);
        }

        var sigmaTot = (double[])degrees.Clone();
        var changed = true;

        // Every move strictly raises modularity, a partition is finite, and each sweep only moves a node when
        // its gain clears the threshold, so sweeps end by convergence — never by a cut-off.
        while (changed)
        {
            changed = false;

            for (var node = 0; node < count; node++)
            {
                var own = community[node];
                var ki = degrees[node];
                sigmaTot[own] -= ki;

                var bestGain = 0d;
                var bestCommunity = own;

                var neighbourWeights = new Dictionary<int, double>();
                foreach (var (next, weight) in adjacency[node])
                {
                    if (next == node)
                    {
                        continue;
                    }

                    neighbourWeights[community[next]] = neighbourWeights.GetValueOrDefault(community[next]) + weight;
                }

                foreach (var (candidate, kiIn) in neighbourWeights.OrderBy(pair => pair.Key))
                {
                    var gain = kiIn - (sigmaTot[candidate] * ki) / totalWeight;
                    if (gain > bestGain + GainThreshold)
                    {
                        bestGain = gain;
                        bestCommunity = candidate;
                    }
                }

                if (bestCommunity != own)
                {
                    community[node] = bestCommunity;
                    sigmaTot[bestCommunity] += ki;
                    changed = true;
                }
                else
                {
                    sigmaTot[own] += ki;
                }
            }
        }

        return community;
    }

    /// <summary>
    /// Computes the modularity Q = Σ_c [(Σ_in_c / 2m) − (Σ_tot_c / 2m)²]. Σ_in_c is the sub-matrix
    /// sum of edges inside community c, so each undirected internal edge counts twice — once from
    /// each endpoint — because the adjacency carries both directions of every edge.
    /// </summary>
    private static double ComputeModularity(int[] community, (int, double)[][] adjacency, double totalWeight)
    {
        if (totalWeight <= 0)
        {
            return 0;
        }

        var groups = community.Distinct().ToArray();
        var sumsIn = new Dictionary<int, double>();
        var sumsTot = new Dictionary<int, double>();

        for (var node = 0; node < community.Length; node++)
        {
            var label = community[node];
            sumsTot[label] = sumsTot.GetValueOrDefault(label) + adjacency[node].Sum(pair => pair.Item2);
            foreach (var (next, weight) in adjacency[node])
            {
                if (community[next] == label)
                {
                    sumsIn[label] = sumsIn.GetValueOrDefault(label) + weight;
                }
            }
        }

        var q = 0d;
        var twoM = totalWeight;
        foreach (var label in groups)
        {
            var internalEdges = sumsIn.GetValueOrDefault(label);
            var total = sumsTot.GetValueOrDefault(label);
            q += (internalEdges / twoM) - Math.Pow(total / twoM, 2);
        }

        return q;
    }

    /// <summary>
    /// Merges any cluster smaller than MinimumClusterSize into the strongest neighbouring cluster: the
    /// neighbour it shares the most relation weight with, ties going to the lowest cluster number.
    /// </summary>
    private static void MergeSmallClusters(int[] community, (int, double)[][] adjacency, int count)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var sizes = new Dictionary<int, int>();
            for (var node = 0; node < count; node++)
            {
                sizes[community[node]] = sizes.GetValueOrDefault(community[node]) + 1;
            }

            var any = false;
            for (var node = 0; node < count; node++)
            {
                var label = community[node];
                if (sizes[label] >= GraphSearchBudget.MinimumClusterSize)
                {
                    continue;
                }

                var bestTarget = -1;
                var bestWeight = 0d;
                foreach (var (next, weight) in adjacency[node])
                {
                    var target = community[next];

                    // The neighbour sharing the most relation weight; ties go to the lowest cluster number.
                    if (target == label || sizes.GetValueOrDefault(target, 0) < GraphSearchBudget.MinimumClusterSize)
                    {
                        continue;
                    }

                    if (weight > bestWeight || (weight == bestWeight && bestTarget >= 0 && target < bestTarget))
                    {
                        bestWeight = weight;
                        bestTarget = target;
                    }
                }

                if (bestTarget >= 0)
                {
                    community[node] = bestTarget;
                    sizes[label] -= 1;
                    sizes[bestTarget] += 1;
                    any = true;
                }
            }

            if (!any)
            {
                break;
            }
        }
    }

    /// <summary>Relabels cluster numbers 0, 1, 2 … by first appearance; returns the distinct count.</summary>
    private static int RelabelClusters(int[] community, int count)
    {
        var map = new Dictionary<int, int>();
        var next = 0;
        for (var node = 0; node < count; node++)
        {
            if (!map.ContainsKey(community[node]))
            {
                map[community[node]] = next++;
            }

            community[node] = map[community[node]];
        }

        return next;
    }

    private static IReadOnlyList<IReadOnlyList<int>> BuildMembers(int[] community, int size, int count)
    {
        var buckets = new List<int>[size];
        for (var at = 0; at < size; at++)
        {
            buckets[at] = [];
        }

        for (var node = 0; node < count; node++)
        {
            buckets[community[node]].Add(node);
        }

        return buckets.Select(list => (IReadOnlyList<int>)list).ToArray();
    }
}
