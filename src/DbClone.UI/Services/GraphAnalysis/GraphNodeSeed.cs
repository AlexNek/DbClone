using DbClone.Application.Interchange;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// One table before it is numbered: the catalog's own identity, the parts its reference states, and
/// the columns only a captured table has.
/// </summary>
/// <param name="Id">The catalog's identity for the table, compared ordinally and never by name.</param>
/// <param name="Schema">The schema the reference states, or <see langword="null"/>.</param>
/// <param name="Table">The table name the reference states, or <see langword="null"/>.</param>
/// <param name="DisplayName">The name a reader sees, stated once so nothing re-derives it.</param>
/// <param name="IsCaptured">Whether this catalog holds the table.</param>
/// <param name="RowCount">The stated row count, or <see langword="null"/>.</param>
/// <param name="Columns">The captured columns, empty for a table the catalog does not hold.</param>
/// <param name="PrimaryKey">The table's primary key, or <see langword="null"/> when it states none.</param>
/// <param name="UniqueKeys">The unique constraints the table declares.</param>
/// <param name="Indexes">The captured index definitions.</param>
/// <param name="CheckConstraints">The check constraints the table declares.</param>
internal sealed record GraphNodeSeed(
    string Id,
    string? Schema,
    string? Table,
    string DisplayName,
    bool IsCaptured,
    long? RowCount,
    IReadOnlyList<CatalogColumnView> Columns,
    CatalogKeyView? PrimaryKey,
    IReadOnlyList<CatalogKeyView> UniqueKeys,
    IReadOnlyList<CatalogIndexView> Indexes,
    IReadOnlyList<CatalogCheckConstraintView> CheckConstraints);
