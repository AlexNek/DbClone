using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// What the picture opens on, by the stated order: the reader's own selection, then the most related
/// table whose closed neighbourhood is still legible, then no focus at all with an explanation and a
/// count — and the whole database whenever it is small enough to read at once (D8).
/// </summary>
/// <remarks>
/// The choice never falls to the biggest hub by itself. A table whose neighbourhood is too large to
/// read is counted as a dense candidate and skipped, so the opening view is one a reader can start
/// from rather than a wall of edges (T21).
/// </remarks>
internal static class GraphInitialViewPlanner
{
    /// <summary>Decides the opening view.</summary>
    /// <param name="seeds">The ordered nodes.</param>
    /// <param name="edges">Every captured constraint.</param>
    /// <param name="degrees">The constraint counts.</param>
    /// <param name="neighbours">Every node's one-hop set.</param>
    /// <param name="indexById">The identity-to-node map, for the reader's preferred table.</param>
    /// <param name="preferredFocusId">The node the reader already selected, when there is one.</param>
    /// <returns>What is visible when the document opens.</returns>
    internal static CatalogInitialView Plan(
        IReadOnlyList<GraphNodeSeed> seeds,
        IReadOnlyList<CatalogGraphEdge> edges,
        GraphDegrees degrees,
        int[][] neighbours,
        Dictionary<string, int> indexById,
        string? preferredFocusId)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(degrees);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(indexById);

        var tableCount = seeds.Count(seed => seed.IsCaptured);
        var opensWhole = tableCount <= CatalogGraphAnalyzer.InitialWholeGraphTableLimit
            && edges.Count <= CatalogGraphAnalyzer.InitialWholeGraphEdgePerTableLimit * tableCount;

        if (opensWhole)
        {
            return new CatalogInitialView(
                true,
                null,
                [.. Enumerable.Range(0, seeds.Count)],
                0,
                tableCount);
        }

        if (preferredFocusId is not null && indexById.TryGetValue(preferredFocusId, out var preferred))
        {
            return new CatalogInitialView(false, preferred, Visible(preferred, neighbours), 0, tableCount);
        }

        var denseCandidates = 0;
        int? focus = null;
        var mostRelations = 0;
        for (var node = 0; node < seeds.Count; node++)
        {
            var relations = degrees.Out[node] + degrees.In[node];
            if (!seeds[node].IsCaptured || relations == 0)
            {
                continue;
            }

            if (neighbours[node].Length + 1 > CatalogGraphAnalyzer.InitialFocusNodeLimit)
            {
                denseCandidates++;
                continue;
            }

            if (relations > mostRelations)
            {
                mostRelations = relations;
                focus = node;
            }
        }

        return focus is null
            ? new CatalogInitialView(false, null, [], denseCandidates, tableCount)
            : new CatalogInitialView(false, focus, Visible(focus.Value, neighbours), denseCandidates, tableCount);
    }

    private static int[] Visible(int focus, int[][] neighbours) =>
        [.. new SortedSet<int>(neighbours[focus]) { focus }];
}
