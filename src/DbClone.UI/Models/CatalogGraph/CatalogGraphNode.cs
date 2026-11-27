namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// One node in the relationship picture: a table the catalog holds, or a table a constraint names
/// that the catalog does not hold.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Index"/> is the node's position in the analyzer's canonical order and is the only
/// token the emitted markup uses. <see cref="Id"/> is the catalog's own reference and is the
/// identity — it is never a key for lookup by display text, and it travels to the browser only
/// inside the data island.
/// </para>
/// </remarks>
/// <param name="Index">The node's position in the analyzer's canonical order; the markup token.</param>
/// <param name="Id">The catalog reference this node stands for: a captured table's id, or the outside end's.</param>
/// <param name="Schema">The schema the catalog states, or <see langword="null"/> when it states none.</param>
/// <param name="Table">The table name the catalog states, or <see langword="null"/> when it states none.</param>
/// <param name="DisplayName">The name to draw: the table alone when one schema is enough to tell the tables apart, schema-qualified when it is not.</param>
/// <param name="IsCaptured">Whether this catalog holds the table.</param>
/// <param name="RowCount">The row count the catalog states, or <see langword="null"/> when it states none.</param>
/// <param name="DegreeOut">How many constraints leave this table. A self reference counts here.</param>
/// <param name="DegreeIn">How many constraints arrive at this table. A self reference counts here too.</param>
/// <param name="CyclicGroupNumber">The 1-based cyclic group this table belongs to, or null when it is in none.</param>
/// <param name="JoinTableBetween">
/// The words naming this table as the link between two others — <c>join table of orders and products</c>
/// — when its primary key is exactly the union of its foreign keys to two captured tables, and
/// <see langword="null"/> otherwise. No engine declares many-to-many, so the words are an inference and
/// the panel and the tooltip say so (D14.3).
/// </param>
internal sealed record CatalogGraphNode(
    int Index,
    string Id,
    string? Schema,
    string? Table,
    string DisplayName,
    bool IsCaptured,
    long? RowCount,
    int DegreeOut,
    int DegreeIn,
    int? CyclicGroupNumber,
    string? JoinTableBetween);
