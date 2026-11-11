namespace DbClone.Application.Interchange;

/// <summary>
/// The relationship graph of a stored catalog — the tables it holds, the tables it only names,
/// and the foreign keys between them. Derived output: no catalog file is written or changed.
/// </summary>
/// <param name="CatalogPath">The catalog folder the view was read from.</param>
/// <param name="FormatVersion">The interchange format version the catalog declares.</param>
/// <param name="SourceDialect">The engine dialect recorded by the export that produced the catalog.</param>
/// <param name="HasData">
/// <see langword="false"/> for a schema-only run, where no table's row count is stated. The header
/// prints <c>no data captured</c> once for the whole document instead of repeating it per node.
/// </param>
/// <param name="Tables">Captured tables, one per <c>tables.json</c> entry.</param>
/// <param name="OutsideTables">
/// Ends a constraint names that the catalog does not hold. Distinct by the reference's own id, so
/// two nameless targets stay two nodes.
/// </param>
/// <param name="Schemas">The schemas the captured tables belong to, with per-schema counts.</param>
/// <param name="Edges">One edge per captured foreign key, in the constraint's own column order.</param>
/// <param name="UnrelatedTableCount">Captured tables that are not an endpoint of any edge.</param>
public sealed record CatalogGraphView(
    string CatalogPath,
    int FormatVersion,
    string SourceDialect,
    bool HasData,
    IReadOnlyList<CatalogTableView> Tables,
    IReadOnlyList<CatalogOutsideTableView> OutsideTables,
    IReadOnlyList<CatalogSchemaView> Schemas,
    IReadOnlyList<CatalogRelationEdgeView> Edges,
    int UnrelatedTableCount);
