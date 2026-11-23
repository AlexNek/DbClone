using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The facts of a graph the seeds need and nothing else: card widths, constraints, connected components,
/// strongly connected groups and a rank per node. Everything is derived in index order, so the profile is
/// the same on every run and under every culture.
/// </summary>
internal sealed class GraphLayoutProfile
{
    private IReadOnlyList<IReadOnlyList<int>>? _clusterGroups;

    private GraphLayoutProfile(
        IReadOnlyList<double> widths,
        IReadOnlyList<(int Child, int Parent)> edges,
        IReadOnlyList<int> component,
        IReadOnlyList<IReadOnlyList<int>> componentNodes,
        IReadOnlyList<int> group,
        IReadOnlyList<int> groupSize,
        IReadOnlyList<int> rank,
        IReadOnlyList<IReadOnlyList<int>> neighbours)
    {
        Widths = widths;
        Edges = edges;
        Component = component;
        ComponentNodes = componentNodes;
        Group = group;
        GroupSize = groupSize;
        Rank = rank;
        Neighbours = neighbours;
    }

    /// <summary>Gets how many cards the graph has.</summary>
    public int NodeCount => Widths.Count;

    /// <summary>Gets each card's width; every card shares <see cref="GraphCard.Height"/>.</summary>
    public IReadOnlyList<double> Widths { get; }

    /// <summary>Gets the constraints as child and parent node indexes, in edge-index order.</summary>
    public IReadOnlyList<(int Child, int Parent)> Edges { get; }

    /// <summary>Gets each node's connected-component number, ordered by the component's lowest node index.</summary>
    public IReadOnlyList<int> Component { get; }

    /// <summary>Gets the nodes of every connected component, ascending.</summary>
    public IReadOnlyList<IReadOnlyList<int>> ComponentNodes { get; }

    /// <summary>Gets each node's strongly connected group number.</summary>
    public IReadOnlyList<int> Group { get; }

    /// <summary>Gets the size of each node's strongly connected group; 1 for a node in no cycle.</summary>
    public IReadOnlyList<int> GroupSize { get; }

    /// <summary>Gets each node's rank: 0 for a table that names nobody, one more than the nearest table it names otherwise.</summary>
    public IReadOnlyList<int> Rank { get; }

    /// <summary>Gets each node's distinct neighbours, either direction, ascending, never the node itself.</summary>
    public IReadOnlyList<IReadOnlyList<int>> Neighbours { get; }

    /// <summary>
    /// Gets every community the components split into, each as its member node indexes in ascending order,
    /// flattened across all components. A component the clustering guards reject contributes nothing, so the
    /// list only holds groups the layout could actually draw as separated boxes. Computed once and cached: it
    /// depends only on the edges, never on a candidate's coordinates, so every layout of this profile competes
    /// against the same partition.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> ClusterGroups => _clusterGroups ??= DetectAllClusters();

    /// <summary>One connected component as a graph of its own.</summary>
    /// <param name="component">The component number.</param>
    /// <returns>The component's profile and the maps back to this graph's node and edge indexes.</returns>
    public GraphSubgraph Subgraph(int component)
    {
        var nodes = ComponentNodes[component];
        var local = new Dictionary<int, int>();
        for (var at = 0; at < nodes.Count; at++)
        {
            local[nodes[at]] = at;
        }

        var edges = Enumerable.Range(0, Edges.Count).Where(edge => local.ContainsKey(Edges[edge].Child)).ToArray();

        return new GraphSubgraph(
            Create(nodes.Select(node => Widths[node]).ToArray(), edges.Select(edge => (local[Edges[edge].Child], local[Edges[edge].Parent])).ToArray()),
            nodes,
            edges);
    }

    /// <summary>Builds the profile of an analyzed graph.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="widths">The width of every card, in node-index order.</param>
    /// <returns>The profile.</returns>
    public static GraphLayoutProfile From(CatalogGraphModel model, IReadOnlyList<double> widths)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Create(widths, model.Edges.Select(edge => (edge.ChildIndex, edge.ParentIndex)).ToArray());
    }

    /// <summary>Builds the profile of a plain graph.</summary>
    /// <param name="widths">The width of every card.</param>
    /// <param name="edges">The constraints as child and parent node indexes.</param>
    /// <returns>The profile.</returns>
    public static GraphLayoutProfile Create(IReadOnlyList<double> widths, IReadOnlyList<(int Child, int Parent)> edges)
    {
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(edges);

        var count = widths.Count;
        var neighbours = Neighbourhoods(count, edges);
        var (component, componentNodes) = Components(count, neighbours);
        var group = StronglyConnectedComponents.Compute(count, edges.Select(edge => (edge.Child, edge.Parent)).ToArray());
        var groupSize = new int[count];
        var sizes = group.GroupBy(number => number).ToDictionary(members => members.Key, members => members.Count());
        for (var node = 0; node < count; node++)
        {
            groupSize[node] = sizes[group[node]];
        }

        return new GraphLayoutProfile(widths, edges, component, componentNodes, group, groupSize, Ranks(count, edges, group), neighbours);
    }

    /// <summary>Detects the communities of every component, mapping each cluster's members back to global node indexes.</summary>
    private IReadOnlyList<IReadOnlyList<int>> DetectAllClusters()
    {
        var groups = new List<IReadOnlyList<int>>();
        for (var component = 0; component < ComponentNodes.Count; component++)
        {
            var sub = Subgraph(component);
            var localNodes = Enumerable.Range(0, sub.Nodes.Count).ToArray();
            var localEdges = Enumerable.Range(0, sub.Edges.Count).ToArray();
            var clustering = GraphClusterDetector.Detect(sub.Profile, localNodes, localEdges);
            if (clustering is null)
            {
                continue;
            }

            foreach (var members in clustering.Members)
            {
                groups.Add(members.Select(local => sub.Nodes[local]).OrderBy(node => node).ToArray());
            }
        }

        return groups;
    }

    private static IReadOnlyList<int>[] Neighbourhoods(int count, IReadOnlyList<(int Child, int Parent)> edges)
    {
        var sets = new SortedSet<int>[count];
        for (var node = 0; node < count; node++)
        {
            sets[node] = [];
        }

        foreach (var (child, parent) in edges)
        {
            if (child != parent)
            {
                sets[child].Add(parent);
                sets[parent].Add(child);
            }
        }

        return sets.Select(set => (IReadOnlyList<int>)set.ToArray()).ToArray();
    }

    private static (int[] Component, IReadOnlyList<IReadOnlyList<int>> Nodes) Components(int count, IReadOnlyList<IReadOnlyList<int>> neighbours)
    {
        var component = new int[count];
        Array.Fill(component, -1);
        var nodes = new List<IReadOnlyList<int>>();

        for (var root = 0; root < count; root++)
        {
            if (component[root] != -1)
            {
                continue;
            }

            var members = new List<int>();
            var pending = new Stack<int>();
            pending.Push(root);
            component[root] = nodes.Count;

            while (pending.Count > 0)
            {
                var node = pending.Pop();
                members.Add(node);
                foreach (var next in neighbours[node].Where(next => component[next] == -1))
                {
                    component[next] = nodes.Count;
                    pending.Push(next);
                }
            }

            members.Sort();
            nodes.Add(members);
        }

        return (component, nodes);
    }

    /// <summary>
    /// Nearest-path rank over the condensation, so a cycle is one level and never a loop in the order, and a
    /// table that names several tables stands one band under the nearest of them instead of one band under
    /// the deepest. A table's children therefore share its own band: a fan of tables pointing at one table
    /// lines up in the single band under it, where each of them reaches a port of its own, instead of
    /// trailing away as a staircase the fan cannot be drawn through.
    /// </summary>
    private static int[] Ranks(int count, IReadOnlyList<(int Child, int Parent)> edges, IReadOnlyList<int> group)
    {
        var groups = group.Count == 0 ? 0 : group.Max() + 1;
        var lowest = Enumerable.Repeat(int.MaxValue, groups).ToArray();
        for (var node = 0; node < count; node++)
        {
            lowest[group[node]] = Math.Min(lowest[group[node]], node);
        }

        var parents = new SortedSet<int>[groups];
        var children = new SortedSet<int>[groups];
        for (var number = 0; number < groups; number++)
        {
            parents[number] = [];
            children[number] = [];
        }

        foreach (var (child, parent) in edges)
        {
            if (group[child] != group[parent])
            {
                parents[group[child]].Add(group[parent]);
                children[group[parent]].Add(group[child]);
            }
        }

        var remaining = parents.Select(set => set.Count).ToArray();
        var level = parents.Select(set => set.Count == 0 ? 0 : int.MaxValue).ToArray();
        var ready = new SortedSet<(int Lowest, int Group)>();
        for (var number = 0; number < groups; number++)
        {
            if (remaining[number] == 0)
            {
                ready.Add((lowest[number], number));
            }
        }

        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            foreach (var child in children[next.Group])
            {
                level[child] = Math.Min(level[child], level[next.Group] + 1);
                if (--remaining[child] == 0)
                {
                    ready.Add((lowest[child], child));
                }
            }
        }

        return group.Select(number => level[number]).ToArray();
    }
}
