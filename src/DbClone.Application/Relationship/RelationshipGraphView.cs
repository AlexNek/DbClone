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
    int UnrelatedTableCount);
