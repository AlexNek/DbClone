namespace DbClone.Application.Interchange;

/// <summary>
/// One captured table in a catalog relationship view.
/// </summary>
/// <param name="Id">
/// <c>TableDefinition.Ref.Value</c> verbatim — the node key. Quote- and case-exact by design, so a
/// value differing only in case names a different table; safe as a key, unsafe as a label.
/// </param>
/// <param name="Schema">The owning schema name, for display only.</param>
/// <param name="Table">The table name, for display only.</param>
/// <param name="RowCount">
/// <see langword="null"/> when the run captured no data for this table; <c>0</c> when it captured the
/// table and it is empty — two facts the renderer prints differently.
/// </param>
/// <param name="ColumnCount">The number of captured columns.</param>
/// <param name="IndexCount">The number of captured indexes: always equal to <c>Indexes.Count</c>.</param>
/// <param name="ForeignKeyCount">The number of constraints this table declares.</param>
/// <param name="Columns">The captured columns, in ordinal position.</param>
/// <param name="PrimaryKey">The table's primary key, or <see langword="null"/> when it states none.</param>
/// <param name="UniqueKeys">The unique constraints the table declares, by name.</param>
/// <param name="Indexes">
/// The captured index definitions. An index states its name, its uniqueness and the key columns it
/// covers, plus the definition text the producer captured — which is what a restore replays and what
/// still carries what the column list cannot, such as a partial index's predicate.
/// </param>
/// <param name="CheckConstraints">The check constraints the table declares, with their expressions as captured.</param>
public sealed record CatalogTableView(
    string Id,
    string Schema,
    string Table,
    long? RowCount,
    int ColumnCount,
    int IndexCount,
    int ForeignKeyCount,
    IReadOnlyList<CatalogColumnView> Columns,
    CatalogKeyView? PrimaryKey,
    IReadOnlyList<CatalogKeyView> UniqueKeys,
    IReadOnlyList<CatalogIndexView> Indexes,
    IReadOnlyList<CatalogCheckConstraintView> CheckConstraints);
