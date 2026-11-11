using DbClone.Application.Interchange;
using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// The node set: the tables the catalog holds, then the tables it only names, each ordinal by
/// identity — the order that makes a shuffled input draw the same picture.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is keyed, merged or drawn by display text. A reference that states no name still gets
/// its own node, and one name in two schemas stays two nodes (D3, FR4b, T18).
/// </para>
/// <para>
/// The name is the shortest text that still tells the tables apart (FR4b, pass 5): the schema prefix is
/// printed when the picture states more than one schema, or when two nodes would otherwise read the same,
/// and omitted when one schema is enough — so a one-schema catalog does not repeat its schema on every
/// card while the cluster bar already states it.
/// </para>
/// </remarks>
internal static class GraphNodeSeeds
{
    /// <summary>Orders the catalog's tables into the canonical node list.</summary>
    /// <param name="view">The facts read from the catalog folder.</param>
    /// <returns>Every node, captured tables first.</returns>
    internal static List<GraphNodeSeed> Order(CatalogGraphView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var qualify = SchemaIsNeeded(view);
        var seeds = new List<GraphNodeSeed>(view.Tables.Count + view.OutsideTables.Count);
        foreach (var table in view.Tables.OrderBy(table => table.Id, StringComparer.Ordinal))
        {
            seeds.Add(new GraphNodeSeed(
                table.Id,
                table.Schema,
                table.Table,
                DisplayName(table.Schema, table.Table, qualify),
                true,
                table.RowCount,
                table.Columns,
                table.PrimaryKey,
                table.UniqueKeys,
                table.Indexes,
                table.CheckConstraints));
        }

        var known = new HashSet<string>(seeds.Select(seed => seed.Id), StringComparer.Ordinal);
        foreach (var outside in view.OutsideTables.OrderBy(table => table.Id, StringComparer.Ordinal))
        {
            if (!known.Add(outside.Id))
            {
                continue;
            }

            seeds.Add(new GraphNodeSeed(
                outside.Id,
                outside.Schema,
                outside.Table,
                DisplayName(outside.Schema, outside.Table, qualify),
                false,
                null,
                [],
                null,
                [],
                [],
                []));
        }

        return seeds;
    }

    /// <summary>The name a reader sees: schema and table when that is what separates the two, never a guess.</summary>
    /// <param name="schema">The stated schema, if any.</param>
    /// <param name="table">The stated table name, if any.</param>
    /// <param name="qualify">Whether the picture holds a schema the prefix is needed for.</param>
    /// <returns>The display name, with the platform's own words for a nameless reference.</returns>
    internal static string DisplayName(string? schema, string? table, bool qualify)
    {
        if (string.IsNullOrEmpty(table))
        {
            return string.IsNullOrEmpty(schema) ? CatalogGraphAnalyzer.UnnamedTableLabel : schema;
        }

        return string.IsNullOrEmpty(schema) || !qualify ? table : $"{schema}.{table}";
    }

    /// <summary>
    /// Whether the schema must be printed for the names to tell the tables apart: the node set states
    /// more than one schema, or two of its tables would read the same without the prefix — a reference
    /// that states no schema can still collide with a captured <c>public.customers</c>.
    /// </summary>
    private static bool SchemaIsNeeded(CatalogGraphView view)
    {
        var schemas = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var collides = false;

        foreach (var (schema, table) in Parts(view))
        {
            if (!string.IsNullOrEmpty(schema))
            {
                schemas.Add(schema);
            }

            if (!string.IsNullOrEmpty(table))
            {
                collides |= !names.Add(table);
            }
        }

        return schemas.Count > 1 || collides;
    }

    private static IEnumerable<(string? Schema, string? Table)> Parts(CatalogGraphView view)
    {
        foreach (var table in view.Tables)
        {
            yield return (table.Schema, table.Table);
        }

        foreach (var outside in view.OutsideTables)
        {
            yield return (outside.Schema, outside.Table);
        }
    }

    /// <summary>Maps the catalog's identity to the node number it was given.</summary>
    /// <param name="seeds">The ordered nodes.</param>
    /// <returns>Identity to index, ordinal.</returns>
    internal static Dictionary<string, int> Index(IReadOnlyList<GraphNodeSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        var indexById = new Dictionary<string, int>(seeds.Count, StringComparer.Ordinal);
        for (var index = 0; index < seeds.Count; index++)
        {
            indexById[seeds[index].Id] = index;
        }

        return indexById;
    }

    /// <summary>
    /// Names the tables that are only a link between two others: a captured table whose primary key
    /// is exactly the union of its foreign-key columns, with at least two constraints to two different
    /// captured tables — a self reference is not a link (D14.3).
    /// </summary>
    /// <param name="seeds">The ordered nodes.</param>
    /// <param name="edges">The constraints, in canonical order.</param>
    /// <returns>The words per node, or <see langword="null"/> where the table is not a link.</returns>
    /// <remarks>
    /// No engine declares many-to-many, so this is an inference and the panel and the tooltip say so.
    /// A link table carrying a surrogate key or a discriminator column is not named, and no guess is
    /// made in its place: silence is the honest form of an inference the catalog cannot support.
    /// </remarks>
    internal static string?[] JoinTables(
        IReadOnlyList<GraphNodeSeed> seeds,
        IReadOnlyList<CatalogGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(edges);

        var join = new string?[seeds.Count];
        for (var node = 0; node < seeds.Count; node++)
        {
            var seed = seeds[node];
            if (seed.PrimaryKey is not { } key || key.Columns.Count == 0)
            {
                continue;
            }

            var outgoing = edges
                .Where(edge => edge.ChildIndex == node && !edge.IsSelfReference)
                .ToList();
            var parents = outgoing
                .Where(edge => edge.ParentIsCaptured)
                .Select(edge => edge.ParentIndex)
                .Distinct()
                .OrderBy(parent => seeds[parent].DisplayName, StringComparer.Ordinal)
                .ToList();
            if (parents.Count < 2)
            {
                continue;
            }

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in outgoing)
            {
                referenced.UnionWith(edge.Columns);
            }

            if (!IsExactly(key.Columns, referenced))
            {
                continue;
            }

            join[node] = $"join table of {seeds[parents[0]].DisplayName} and {seeds[parents[1]].DisplayName}";
        }

        return join;
    }

    private static bool IsExactly(IReadOnlyList<string> key, HashSet<string> columns) =>
        key.Count == columns.Count && key.All(columns.Contains);

    /// <summary>Numbers the nodes and attaches their degrees and their cyclic group.</summary>
    /// <param name="seeds">The ordered nodes.</param>
    /// <param name="degrees">The two constraint counts per node.</param>
    /// <param name="groupOfNode">The cyclic group per node, where it has one.</param>
    /// <param name="joinOfNode">The words naming a link table per node, where it is one.</param>
    /// <returns>The nodes every later stage reads.</returns>
    internal static List<CatalogGraphNode> Number(
        IReadOnlyList<GraphNodeSeed> seeds,
        GraphDegrees degrees,
        int?[] groupOfNode,
        string?[] joinOfNode)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(degrees);
        ArgumentNullException.ThrowIfNull(groupOfNode);
        ArgumentNullException.ThrowIfNull(joinOfNode);

        var nodes = new List<CatalogGraphNode>(seeds.Count);
        for (var node = 0; node < seeds.Count; node++)
        {
            var seed = seeds[node];
            nodes.Add(new CatalogGraphNode(
                node,
                seed.Id,
                seed.Schema,
                seed.Table,
                seed.DisplayName,
                seed.IsCaptured,
                seed.RowCount,
                degrees.Out[node],
                degrees.In[node],
                groupOfNode[node],
                joinOfNode[node]));
        }

        return nodes;
    }
}
