namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// One captured foreign key, drawn as one edge. Two constraints between the same pair of tables are
/// two edges: merging them would lose the fact the register prints.
/// </summary>
/// <param name="Index">The edge's position in the analyzer's canonical order; the markup token.</param>
/// <param name="ChildIndex">The node that stores the value.</param>
/// <param name="ParentIndex">The node the value comes from.</param>
/// <param name="Name">The constraint's captured name, or the unnamed-constraint words when it states none.</param>
/// <param name="Label">The full text of the constraint, as the register and the tooltip spell it.</param>
/// <param name="PictureLabel">
/// The same constraint cut down to the two column lists. It is written beside the route and hidden,
/// shown only when the reader points at the arrow or selects it: the tables it joins are already
/// named by the cards at either end, and text along every line is what made the picture unreadable.
/// </param>
/// <param name="Columns">The referencing column names, in the constraint's own order.</param>
/// <param name="ReferencedColumns">The referenced column names, in the constraint's own order.</param>
/// <param name="OnDelete">The <c>ON DELETE</c> text, or <see langword="null"/> when it leaves the default.</param>
/// <param name="OnUpdate">The <c>ON UPDATE</c> text, or <see langword="null"/> when it leaves the default.</param>
/// <param name="IsMandatory">Whether every referencing column is non-nullable.</param>
/// <param name="IsSelfReference">Whether the parent is the child's own table.</param>
/// <param name="ParentIsCaptured">Whether the parent is a table this catalog holds.</param>
/// <param name="IsOneToOne">
/// Whether some key of the child has exactly the referencing columns as its column set, so at most
/// one child row can name a parent row. A unique index alone is not proof: the catalog records its
/// columns but nothing about its predicate, so a partial unique index would be read as a key over the
/// whole table (D14.2).
/// </param>
/// <param name="IsExactOne">
/// Whether <see cref="IsOneToOne"/> holds and every referencing column is non-nullable, so exactly
/// one child row names each parent row.
/// </param>
/// <param name="ParallelOrdinal">The edge's position within the set joining the same pair.</param>
/// <param name="ParallelCount">How many constraints join this same pair of tables.</param>
internal sealed record CatalogGraphEdge(
    int Index,
    int ChildIndex,
    int ParentIndex,
    string Name,
    string Label,
    string PictureLabel,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> ReferencedColumns,
    string? OnDelete,
    string? OnUpdate,
    bool IsMandatory,
    bool IsSelfReference,
    bool ParentIsCaptured,
    bool IsOneToOne,
    bool IsExactOne,
    int ParallelOrdinal,
    int ParallelCount)
{
    /// <summary>
    /// Gets whether more than one constraint joins this pair, which is why no one of them may be
    /// drawn as a single line.
    /// </summary>
    public bool IsParallel => ParallelCount > 1;
}
