using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// The cluster selector's chips, in bar order, and the claim set that says which chips own each
/// table. A table may be claimed by more than one chip and stays visible while any claiming chip is
/// on; a table no chip claims is never hidden (FR15, D8a).
/// </summary>
/// <remarks>
/// The schema set is the union of the schemas the catalog states and the schemas its captured tables
/// point at, so a chip is never missing for a table that is drawn.
/// </remarks>
internal static class GraphClusterChips
{
    /// <summary>Builds the chips and the per-node claims together, because neither is useful alone.</summary>
    /// <param name="view">The facts read from the catalog folder, for the stated schemas.</param>
    /// <param name="seeds">The ordered nodes.</param>
    /// <param name="groups">The cyclic groups, each of which becomes a chip.</param>
    /// <param name="degrees">The constraint counts, which decide which tables are unconnected.</param>
    /// <param name="chipsByNode">Every chip claiming each node.</param>
    /// <returns>The chips, numbered in bar order.</returns>
    internal static List<CatalogClusterChip> Build(
        RelationshipGraphView view,
        IReadOnlyList<GraphNodeSeed> seeds,
        IReadOnlyList<CatalogCyclicGroup> groups,
        GraphDegrees degrees,
        out Dictionary<int, IReadOnlyList<int>> chipsByNode)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(degrees);

        var claims = new List<int>[seeds.Count];
        for (var node = 0; node < seeds.Count; node++)
        {
            claims[node] = [];
        }

        var chips = new List<CatalogClusterChip>();
        foreach (var schema in SchemaNames(view, seeds))
        {
            var members = new List<int>();
            for (var node = 0; node < seeds.Count; node++)
            {
                if (seeds[node].IsCaptured && string.Equals(seeds[node].Schema, schema, StringComparison.Ordinal))
                {
                    members.Add(node);
                }
            }

            if (members.Count > 0)
            {
                Add(new CatalogClusterChip(chips.Count, EClusterChipKind.Schema, schema, members), members);
            }
        }

        foreach (var group in groups)
        {
            Add(
                new CatalogClusterChip(chips.Count, EClusterChipKind.CyclicGroup, group.Label, group.MemberNodeIndexes),
                group.MemberNodeIndexes);
        }

        var unconnected = new List<int>();
        for (var node = 0; node < seeds.Count; node++)
        {
            if (seeds[node].IsCaptured && degrees.Out[node] == 0 && degrees.In[node] == 0)
            {
                unconnected.Add(node);
            }
        }

        if (unconnected.Count > 0)
        {
            Add(
                new CatalogClusterChip(chips.Count, EClusterChipKind.Unconnected, CatalogGraphAnalyzer.UnconnectedLabel, unconnected),
                unconnected);
        }

        chipsByNode = new Dictionary<int, IReadOnlyList<int>>(seeds.Count);
        for (var node = 0; node < seeds.Count; node++)
        {
            chipsByNode[node] = claims[node];
        }

        return chips;

        void Add(CatalogClusterChip chip, IReadOnlyList<int> members)
        {
            chips.Add(chip);
            foreach (var member in members)
            {
                claims[member].Add(chip.Index);
            }
        }
    }

    /// <summary>
    /// The schema names a chip can be made for: the schemas the catalog states, plus any schema a
    /// captured table states, ordinal.
    /// </summary>
    private static IEnumerable<string> SchemaNames(RelationshipGraphView view, IReadOnlyList<GraphNodeSeed> seeds)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in view.Schemas)
        {
            if (!string.IsNullOrEmpty(row.Schema))
            {
                names.Add(row.Schema);
            }
        }

        foreach (var seed in seeds)
        {
            if (seed.IsCaptured && !string.IsNullOrEmpty(seed.Schema))
            {
                names.Add(seed.Schema!);
            }
        }

        return names;
    }
}
