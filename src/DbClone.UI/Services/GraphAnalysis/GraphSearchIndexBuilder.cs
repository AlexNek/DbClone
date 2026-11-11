using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// Every searchable fact once, in canonical order: a table's own schema, name, columns and comments,
/// then each constraint's name against the table that declares it.
/// </summary>
/// <remarks>
/// A hit is always reported with the field that matched, never guessed from a table name — so
/// <c>customer_id</c> finds the tables that have the column, not only the tables named like it (FR14).
/// </remarks>
internal static class GraphSearchIndexBuilder
{
    /// <summary>Builds the search index.</summary>
    /// <param name="seeds">The ordered nodes.</param>
    /// <param name="edges">Every captured constraint.</param>
    /// <returns>The searchable entries, in canonical order.</returns>
    internal static List<CatalogSearchEntry> Build(
        IReadOnlyList<GraphNodeSeed> seeds,
        IReadOnlyList<CatalogGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(edges);

        var entries = new List<CatalogSearchEntry>();
        for (var node = 0; node < seeds.Count; node++)
        {
            var seed = seeds[node];
            if (!string.IsNullOrEmpty(seed.Schema))
            {
                entries.Add(new CatalogSearchEntry(node, ESearchField.Schema, seed.Schema!));
            }

            if (!string.IsNullOrEmpty(seed.Table))
            {
                entries.Add(new CatalogSearchEntry(node, ESearchField.Table, seed.Table!));
            }

            foreach (var column in seed.Columns)
            {
                entries.Add(new CatalogSearchEntry(node, ESearchField.Column, column.Name));
                if (!string.IsNullOrWhiteSpace(column.Comment))
                {
                    entries.Add(new CatalogSearchEntry(node, ESearchField.Comment, column.Comment!));
                }
            }
        }

        foreach (var edge in edges)
        {
            entries.Add(new CatalogSearchEntry(edge.ChildIndex, ESearchField.Constraint, edge.Name));
        }

        return entries;
    }
}
