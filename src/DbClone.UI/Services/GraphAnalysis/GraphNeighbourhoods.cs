using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// Who each table touches: its one-step neighbourhood in either direction, and the second hop counted
/// from it — the sets a focus reveals and the set the opening view is measured against.
/// </summary>
/// <remarks>
/// A self reference never makes a table its own neighbour: the sets answer "which other tables", and a
/// table that points at itself is already known to the reader from its own loop.
/// </remarks>
internal static class GraphNeighbourhoods
{
    /// <summary>Measures one-hop neighbourhoods for every node.</summary>
    /// <param name="nodeCount">How many nodes the graph has.</param>
    /// <param name="edges">Every captured constraint.</param>
    /// <returns>Per node, the indexes it touches, ordinal and without itself.</returns>
    internal static int[][] Measure(int nodeCount, IReadOnlyList<CatalogGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var sets = new HashSet<int>[nodeCount];
        for (var node = 0; node < nodeCount; node++)
        {
            sets[node] = [];
        }

        foreach (var edge in edges)
        {
            if (edge.ChildIndex == edge.ParentIndex)
            {
                continue;
            }

            sets[edge.ChildIndex].Add(edge.ParentIndex);
            sets[edge.ParentIndex].Add(edge.ChildIndex);
        }

        return [.. sets.Select(set => set.OrderBy(node => node).ToArray())];
    }

    /// <summary>The tables reached in one step from a node's neighbours, itself and its own neighbours excluded.</summary>
    /// <param name="node">The node the second hop is counted from.</param>
    /// <param name="neighbours">Every node's one-hop set.</param>
    /// <returns>The second hop, ordinal.</returns>
    internal static int[] TwoHop(int node, int[][] neighbours)
    {
        ArgumentNullException.ThrowIfNull(neighbours);

        var oneHop = neighbours[node].ToHashSet();
        var reach = new SortedSet<int>();
        foreach (var first in neighbours[node])
        {
            foreach (var second in neighbours[first])
            {
                if (second != node && !oneHop.Contains(second))
                {
                    reach.Add(second);
                }
            }
        }

        return [.. reach];
    }
}
