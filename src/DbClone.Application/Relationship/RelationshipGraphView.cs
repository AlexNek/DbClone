namespace DbClone.Application.Relationship;

/// <summary>
/// The relationship graph of one origin — a stored catalog's tables, or a live database's — the
/// tables its constraints only name, and the foreign keys between them. Derived output: no catalog
/// file is written or changed and no table row is read.
/// </summary>
/// <param name="Origin">Where the graph was read from. The document's words follow its kind.</param>
/// <param name="HasData">
/// <see langword="false"/> for a catalog run that captured no row count and for every live read, which
/// never counts rows. The origin decides which sentence the document and the cards use for it.
/// </param>
/// <param name="Tables">Captured tables, one per table the origin states.</param>
/// <param name="OutsideTables">
/// Ends a constraint names that the origin does not hold. Distinct by the reference's own id, so
/// two nameless targets stay two nodes.
/// </param>
/// <param name="Schemas">The schemas the captured tables belong to, with per-schema counts.</param>
/// <param name="Edges">One edge per captured foreign key, in the constraint's own column order.</param>
/// <param name="UnrelatedTableCount">Captured tables that are not an endpoint of any edge.</param>
public sealed record RelationshipGraphView(
    GraphViewOrigin Origin,
    bool HasData,
    IReadOnlyList<RelationshipTableView> Tables,
    IReadOnlyList<RelationshipOutsideTableView> OutsideTables,
    IReadOnlyList<RelationshipSchemaView> Schemas,
    IReadOnlyList<RelationshipEdgeView> Edges,
    int UnrelatedTableCount)
{
    /// <summary>This graph without the tables a cluster filter switched off, and without what only they stated.</summary>
    /// <remarks>
    /// <para>
    /// A constraint survives only while both of its ends do, so the remaining picture never shows the
    /// shape of a relation whose other end is not drawn. An outside end is kept only while a surviving
    /// constraint still names it, for the same reason.
    /// </para>
    /// <para>
    /// The schema rows and the unrelated count are recomputed from what is left, because both are what
    /// the document prints about the graph it was given; the totals of the whole catalog are stated
    /// separately by whoever asked for the reduced view.
    /// </para>
    /// </remarks>
    /// <param name="hiddenTableIds">The ids of the captured tables to leave out.</param>
    /// <returns>The remaining graph, or this graph when nothing is left out of it.</returns>
    public RelationshipGraphView Without(IReadOnlySet<string> hiddenTableIds)
    {
        ArgumentNullException.ThrowIfNull(hiddenTableIds);

        if (hiddenTableIds.Count == 0)
        {
            return this;
        }

        var tables = Tables
            .Where(table => !hiddenTableIds.Contains(table.Id))
            .ToList();

        var edges = Edges
            .Where(edge => !hiddenTableIds.Contains(edge.ChildId) && !hiddenTableIds.Contains(edge.ParentId))
            .ToList();

        var parents = new HashSet<string>(edges.Select(edge => edge.ParentId), StringComparer.Ordinal);
        var outside = OutsideTables.Where(table => parents.Contains(table.Id)).ToList();

        var related = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            related.Add(edge.ChildId);
            related.Add(edge.ParentId);
        }

        var schemas = tables
            .GroupBy(table => table.Schema, StringComparer.Ordinal)
            .Select(group => new RelationshipSchemaView(
                group.Key,
                group.Count(),
                group.Count(table => related.Contains(table.Id))))
            .OrderBy(row => row.Schema, StringComparer.Ordinal)
            .ToList();

        return new RelationshipGraphView(
            Origin,
            HasData,
            tables,
            outside,
            schemas,
            edges,
            tables.Count(table => !related.Contains(table.Id)));
    }
}
