namespace DbClone.Application.Interchange;

/// <summary>
/// One captured foreign key, drawn as one edge. Two constraints between the same pair of tables
/// are two edges: merging them would lose the fact the register prints.
/// </summary>
/// <param name="ChildId">
/// The captured table that stores the value — always captured, because a captured table declares
/// the constraint.
/// </param>
/// <param name="ParentId">The parent's node id: a captured table's id, or one present in the outside list.</param>
/// <param name="ConstraintName">The constraint's captured name.</param>
/// <param name="Columns">The referencing column names, in the constraint's own order.</param>
/// <param name="ReferencedColumns">The referenced column names, in the constraint's own order.</param>
/// <param name="OnDelete">The <c>ON DELETE</c> action, or <see langword="null"/> when it leaves the default.</param>
/// <param name="OnUpdate">The <c>ON UPDATE</c> action, or <see langword="null"/> when it leaves the default.</param>
/// <param name="IsMandatory">Whether every referencing column is non-nullable.</param>
/// <param name="IsSelfReference">Whether the parent is the child's own table.</param>
/// <param name="ParentIsCaptured">Whether the parent is a table this catalog holds.</param>
public sealed record CatalogRelationEdgeView(
    string ChildId,
    string ParentId,
    string ConstraintName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> ReferencedColumns,
    string? OnDelete,
    string? OnUpdate,
    bool IsMandatory,
    bool IsSelfReference,
    bool ParentIsCaptured);
