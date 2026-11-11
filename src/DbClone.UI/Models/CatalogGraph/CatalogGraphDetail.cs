using DbClone.Application.Interchange;

namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// Everything the detail panel says about one node. For a table the catalog does not hold, every
/// field the folder never described stays empty: the panel states the name and the edges, never a
/// <c>0</c> or a dash standing in for an unknown.
/// </summary>
/// <param name="NodeIndex">The node this detail belongs to.</param>
/// <param name="QualifiedName">The name to print: schema and table when both are stated.</param>
/// <param name="Schema">The schema the catalog states, or <see langword="null"/>.</param>
/// <param name="Table">The table name the catalog states, or <see langword="null"/>.</param>
/// <param name="IsCaptured">Whether this catalog holds the table.</param>
/// <param name="RowCount">
/// The stated row count, or <see langword="null"/> when it is not stated — a captured table whose
/// run recorded no data and an outside table both answer null, and the panel words them apart.
/// </param>
/// <param name="Columns">The captured columns, empty for a table the catalog does not hold.</param>
/// <param name="OutgoingEdgeIndexes">The constraints this table declares.</param>
/// <param name="IncomingEdgeIndexes">The constraints other tables declare against it.</param>
/// <param name="NeighbourNodeIndexes">
/// Every table this one touches in one step, in either direction, itself excluded — the set the
/// JavaScript reveals for a focus, and the set the initial view is measured against.
/// </param>
/// <param name="TwoHopNodeIndexes">
/// The tables reached in one step from <paramref name="NeighbourNodeIndexes"/>, the node itself and
/// its one-step neighbours excluded — the second hop's own answer.
/// </param>
/// <param name="CyclicGroupNumber">The cyclic group this table belongs to, or null when it is in none.</param>
/// <param name="CyclicGroupMembers">The other members of that group, by display name.</param>
/// <param name="PrimaryKey">The table's primary key, or null when the catalog states none.</param>
/// <param name="UniqueKeys">The unique constraints the table declares.</param>
/// <param name="Indexes">The captured indexes, stated by their key columns and by the definition text.</param>
/// <param name="CheckConstraints">The check constraints the table declares.</param>
internal sealed record CatalogGraphDetail(
    int NodeIndex,
    string QualifiedName,
    string? Schema,
    string? Table,
    bool IsCaptured,
    long? RowCount,
    IReadOnlyList<CatalogColumnView> Columns,
    IReadOnlyList<int> OutgoingEdgeIndexes,
    IReadOnlyList<int> IncomingEdgeIndexes,
    IReadOnlyList<int> NeighbourNodeIndexes,
    IReadOnlyList<int> TwoHopNodeIndexes,
    int? CyclicGroupNumber,
    IReadOnlyList<string> CyclicGroupMembers,
    CatalogKeyView? PrimaryKey,
    IReadOnlyList<CatalogKeyView> UniqueKeys,
    IReadOnlyList<CatalogIndexView> Indexes,
    IReadOnlyList<CatalogCheckConstraintView> CheckConstraints);
