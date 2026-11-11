using DbClone.Application.Interchange;
using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// One edge per captured constraint, in canonical order, with the two texts a constraint carries: the
/// full sentence the register and the tooltip print, and the short form the picture prints beside the
/// line.
/// </summary>
/// <remarks>
/// A pair of tables joined more than once keeps every member: each edge knows its ordinal within the
/// pair and how many the pair holds, which is what lets the layout offset them instead of drawing one
/// line and losing a fact (FR13, T10).
/// </remarks>
internal static class GraphEdgeBuilder
{
    /// <summary>Resolves every constraint the catalog states into an edge.</summary>
    /// <param name="view">The facts read from the catalog folder.</param>
    /// <param name="indexById">The identity-to-node map.</param>
    /// <param name="seeds">The ordered nodes, for the parent's display name.</param>
    /// <returns>The edges, numbered in canonical order.</returns>
    internal static List<CatalogGraphEdge> Build(
        CatalogGraphView view,
        Dictionary<string, int> indexById,
        IReadOnlyList<GraphNodeSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(indexById);
        ArgumentNullException.ThrowIfNull(seeds);

        var ordered = view.Edges
            .Select(edge => (
                Edge: edge,
                Child: Resolve(indexById, edge.ChildId, "child"),
                Parent: Resolve(indexById, edge.ParentId, "parent")))
            .OrderBy(entry => entry.Child)
            .ThenBy(entry => entry.Edge.ConstraintName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Parent)
            .ToList();

        var pairSizes = new Dictionary<(int Child, int Parent), int>();
        foreach (var entry in ordered)
        {
            var pair = Pair(entry.Child, entry.Parent);
            pairSizes[pair] = pairSizes.GetValueOrDefault(pair) + 1;
        }

        var taken = new Dictionary<(int Child, int Parent), int>();
        var edges = new List<CatalogGraphEdge>(ordered.Count);
        foreach (var entry in ordered)
        {
            var pair = Pair(entry.Child, entry.Parent);
            var ordinal = taken.GetValueOrDefault(pair);
            taken[pair] = ordinal + 1;

            var parent = seeds[entry.Parent];
            var oneToOne = KeyCoversExactly(seeds[entry.Child], entry.Edge.Columns);
            edges.Add(new CatalogGraphEdge(
                edges.Count,
                entry.Child,
                entry.Parent,
                string.IsNullOrEmpty(entry.Edge.ConstraintName)
                    ? CatalogGraphAnalyzer.UnnamedConstraintLabel
                    : entry.Edge.ConstraintName,
                ConstraintLabel(entry.Edge, parent),
                PictureLabel(entry.Edge),
                entry.Edge.Columns,
                entry.Edge.ReferencedColumns,
                entry.Edge.OnDelete,
                entry.Edge.OnUpdate,
                entry.Edge.IsMandatory,
                entry.Edge.IsSelfReference,
                entry.Edge.ParentIsCaptured,
                oneToOne,
                oneToOne && entry.Edge.IsMandatory,
                ordinal,
                pairSizes[pair]));
        }

        return edges;
    }

    /// <summary>
    /// Whether some key of the child has exactly the referencing columns as its column set, which is
    /// the only proof the catalog carries that a child row names at most one parent row (D14.2).
    /// </summary>
    /// <param name="child">The table that stores the value.</param>
    /// <param name="columns">The referencing columns, in the constraint's own order.</param>
    /// <returns>Whether a primary key or a unique constraint of the child covers exactly these columns.</returns>
    /// <remarks>
    /// Set equality, not containment: a key on <c>(a, b)</c> says nothing about <c>a</c> alone. A
    /// unique index is deliberately not consulted. Its columns are recorded, but the catalog states
    /// nothing about its predicate or its expressions, so a partial unique index would be taken for a
    /// key over the whole table — and a mark that is sometimes missing is better than a mark that is
    /// sometimes wrong.
    /// </remarks>
    private static bool KeyCoversExactly(GraphNodeSeed child, IReadOnlyList<string> columns)
    {
        if (!child.IsCaptured || columns.Count == 0)
        {
            return false;
        }

        if (child.PrimaryKey is { } key && SameColumns(key.Columns, columns))
        {
            return true;
        }

        return child.UniqueKeys.Any(unique => SameColumns(unique.Columns, columns));
    }

    private static bool SameColumns(IReadOnlyList<string> key, IReadOnlyList<string> columns)
    {
        if (key.Count != columns.Count)
        {
            return false;
        }

        var set = new HashSet<string>(key, StringComparer.Ordinal);
        return set.Count == columns.Count && columns.All(set.Contains);
    }

    /// <summary>
    /// The constraint's full text: the columns that store the value, then the table the value comes
    /// from, then the delete action only where it leaves the default.
    /// </summary>
    private static string ConstraintLabel(CatalogRelationEdgeView edge, GraphNodeSeed parent)
    {
        var right = edge.ReferencedColumns.Count == 0
            ? parent.DisplayName
            : $"{parent.DisplayName}.{Join(edge.ReferencedColumns)}";
        var label = $"{Join(edge.Columns)} → {right}";

        return edge.OnDelete is null ? label : $"{label} on delete: {edge.OnDelete.ToLowerInvariant()}";
    }

    /// <summary>
    /// The short text the picture prints beside an edge: the columns, and nothing else. The parent's
    /// name is already on the card the edge ends at, so repeating it only crowds the drawing.
    /// </summary>
    private static string PictureLabel(CatalogRelationEdgeView edge)
    {
        var right = edge.ReferencedColumns.Count == 0 ? "pk" : Join(edge.ReferencedColumns);
        return $"{Join(edge.Columns)} → {right}";
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"({string.Join(", ", names)})";

    private static int Resolve(Dictionary<string, int> indexById, string id, string role) =>
        indexById.TryGetValue(id, out var index)
            ? index
            : throw new InvalidOperationException(
                $"The relationship view names a {role} table that has no node: {id}. Re-read the catalog.");

    private static (int Child, int Parent) Pair(int child, int parent) =>
        child <= parent ? (child, parent) : (parent, child);
}
