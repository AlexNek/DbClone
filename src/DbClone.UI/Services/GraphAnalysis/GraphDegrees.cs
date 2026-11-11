using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// How many constraints each table declares and how many are declared against it, held apart so the
/// two figures are never added together by accident (FR12, D7).
/// </summary>
/// <param name="Out">For each node, the constraints it declares.</param>
/// <param name="In">For each node, the constraints other tables declare against it.</param>
internal sealed record GraphDegrees(int[] Out, int[] In)
{
    /// <summary>Counts both directions of every edge once.</summary>
    /// <param name="nodeCount">How many nodes the graph has.</param>
    /// <param name="edges">Every captured constraint.</param>
    /// <returns>The two counts per node.</returns>
    internal static GraphDegrees Measure(int nodeCount, IReadOnlyList<CatalogGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var degrees = new GraphDegrees(new int[nodeCount], new int[nodeCount]);
        foreach (var edge in edges)
        {
            degrees.Out[edge.ChildIndex]++;
            degrees.In[edge.ParentIndex]++;
        }

        return degrees;
    }
}
