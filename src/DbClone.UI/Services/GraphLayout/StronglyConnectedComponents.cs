namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Finds strongly connected components with an iterated Tarjan pass.
/// </summary>
/// <remarks>
/// <para>
/// The pass is iterative for the product's own stated reason: a catalog may hold a reference chain
/// longer than a recursion would survive. Nothing here recurses, so a 1 000-table chain grows no
/// stack — which is what T17 asserts instead of a wall-clock figure.
/// </para>
/// <para>
/// The numbering is the discovery order, which depends on the order the nodes were given. Callers
/// that need a stable answer renumber by their own canonical order; this pass promises only that
/// two nodes share a number exactly when they reach each other.
/// </para>
/// </remarks>
internal static class StronglyConnectedComponents
{
    /// <summary>Computes the component of every node.</summary>
    /// <param name="nodeCount">How many nodes the graph has.</param>
    /// <param name="edges">The directed edges. A self edge is ignored: a node always reaches itself.</param>
    /// <returns>One component number per node; two nodes share a number exactly when they reach each other.</returns>
    public static int[] Compute(int nodeCount, IReadOnlyList<(int From, int To)> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var adjacency = new List<int>[nodeCount];
        for (var node = 0; node < nodeCount; node++)
        {
            adjacency[node] = [];
        }

        foreach (var (from, to) in edges)
        {
            if (from != to)
            {
                adjacency[from].Add(to);
            }
        }

        var discovery = new int[nodeCount];
        var low = new int[nodeCount];
        var component = new int[nodeCount];
        var onStack = new bool[nodeCount];
        Array.Fill(discovery, -1);
        Array.Fill(component, -1);

        var pending = new Stack<int>();
        var callStack = new Stack<(int Node, int NextChild)>();
        var nextDiscovery = 0;
        var nextComponent = 0;

        for (var root = 0; root < nodeCount; root++)
        {
            if (discovery[root] != -1)
            {
                continue;
            }

            discovery[root] = low[root] = nextDiscovery++;
            pending.Push(root);
            onStack[root] = true;
            callStack.Push((root, 0));

            while (callStack.Count > 0)
            {
                var (node, nextChild) = callStack.Pop();

                if (nextChild < adjacency[node].Count)
                {
                    callStack.Push((node, nextChild + 1));
                    var child = adjacency[node][nextChild];

                    if (discovery[child] == -1)
                    {
                        discovery[child] = low[child] = nextDiscovery++;
                        pending.Push(child);
                        onStack[child] = true;
                        callStack.Push((child, 0));
                    }
                    else if (onStack[child])
                    {
                        low[node] = Math.Min(low[node], discovery[child]);
                    }

                    continue;
                }

                if (low[node] == discovery[node])
                {
                    int member;
                    do
                    {
                        member = pending.Pop();
                        onStack[member] = false;
                        component[member] = nextComponent;
                    }
                    while (member != node);

                    nextComponent++;
                }

                if (callStack.Count > 0)
                {
                    var parent = callStack.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
            }
        }

        return component;
    }
}
