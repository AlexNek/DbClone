using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// Everything the panel says about one table: its columns, its own definitions (keys, indexes and
/// checks), the constraints it declares and receives, its neighbourhood to two hops, and its cyclic
/// group. A table the catalog does not hold keeps every field the folder never described empty —
/// never a zero and never a dash standing in for an unknown (FR4, D7).
/// </summary>
internal static class GraphDetailBuilder
{
    /// <summary>Builds one detail per node.</summary>
    /// <param name="seeds">The ordered nodes.</param>
    /// <param name="edges">Every captured constraint.</param>
    /// <param name="neighbours">Every node's one-hop set.</param>
    /// <param name="groups">The cyclic groups.</param>
    /// <param name="groupOfNode">For each node, its group number.</param>
    /// <returns>The details, keyed by node index.</returns>
    internal static Dictionary<int, CatalogGraphDetail> Build(
        IReadOnlyList<GraphNodeSeed> seeds,
        IReadOnlyList<CatalogGraphEdge> edges,
        int[][] neighbours,
        IReadOnlyList<CatalogCyclicGroup> groups,
        int?[] groupOfNode)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(groupOfNode);

        var outgoing = new List<int>[seeds.Count];
        var incoming = new List<int>[seeds.Count];
        for (var node = 0; node < seeds.Count; node++)
        {
            outgoing[node] = [];
            incoming[node] = [];
        }

        foreach (var edge in edges)
        {
            outgoing[edge.ChildIndex].Add(edge.Index);
            incoming[edge.ParentIndex].Add(edge.Index);
        }

        var details = new Dictionary<int, CatalogGraphDetail>(seeds.Count);
        for (var node = 0; node < seeds.Count; node++)
        {
            var seed = seeds[node];
            details[node] = new CatalogGraphDetail(
                node,
                seed.DisplayName,
                seed.Schema,
                seed.Table,
                seed.IsCaptured,
                seed.RowCount,
                seed.Columns,
                outgoing[node],
                incoming[node],
                neighbours[node],
                GraphNeighbourhoods.TwoHop(node, neighbours),
                groupOfNode[node],
                GraphCyclicGroups.OtherMembers(node, groupOfNode[node], groups, seeds),
                seed.PrimaryKey,
                seed.UniqueKeys,
                seed.Indexes,
                seed.CheckConstraints);
        }

        return details;
    }
}
