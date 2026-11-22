using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// What the picture opens on: every table the model holds, unless the reader opened the view from a
/// table they had already selected — the one narrowing a reader asks for themselves (D8).
/// </summary>
/// <remarks>
/// No count, density or legibility rule holds a table back from the first picture: a catalog of any
/// size opens whole, because the reader is the one who decides what they can read. A selected table is
/// a request about that table, so the picture then opens on it and its one-step neighbours; every other
/// node is still emitted and revealed by <c>Show the whole database</c>, a chip or a search, so the rest
/// of the catalog is a click away rather than a second read.
/// </remarks>
internal static class GraphInitialViewPlanner
{
    /// <summary>Decides the opening view.</summary>
    /// <param name="seeds">The ordered nodes.</param>
    /// <param name="neighbours">Every node's one-hop set.</param>
    /// <param name="indexById">The identity-to-node map, for the reader's preferred table.</param>
    /// <param name="preferredFocusId">The node the reader already selected, when there is one.</param>
    /// <returns>What is visible when the document opens.</returns>
    internal static CatalogInitialView Plan(
        IReadOnlyList<GraphNodeSeed> seeds,
        int[][] neighbours,
        Dictionary<string, int> indexById,
        string? preferredFocusId)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(indexById);

        var tableCount = seeds.Count(seed => seed.IsCaptured);

        if (preferredFocusId is not null && indexById.TryGetValue(preferredFocusId, out var preferred))
        {
            return new CatalogInitialView(false, preferred, Visible(preferred, neighbours), tableCount);
        }

        return new CatalogInitialView(true, null, [.. Enumerable.Range(0, seeds.Count)], tableCount);
    }

    private static int[] Visible(int focus, int[][] neighbours) =>
        [.. new SortedSet<int>(neighbours[focus]) { focus }];
}
