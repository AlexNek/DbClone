namespace DbClone.Application.Relationship;

/// <summary>
/// One captured column in a relationship view.
/// </summary>
/// <param name="Name">The column name as the origin states it.</param>
/// <param name="LogicalType">The logical type the origin records, as text.</param>
/// <param name="IsNullable">Whether the column admits null values.</param>
/// <param name="IsPartOfPrimaryKey">Whether the column participates in the table's primary key.</param>
/// <param name="IsReferencingForeignKey">
/// Whether the column appears in a constraint this table declares. Never merged with
/// <paramref name="IsReferencedByForeignKey"/>: a column can be a referenced target without
/// declaring anything.
/// </param>
/// <param name="IsReferencedByForeignKey">
/// Whether the column is a target of another table's constraint (the constraint's
/// <c>ReferencedColumns</c>).
/// </param>
/// <param name="Comment">The captured column comment, or <see langword="null"/> when none was captured.</param>
public sealed record RelationshipColumnView(
    string Name,
    string LogicalType,
    bool IsNullable,
    bool IsPartOfPrimaryKey,
    bool IsReferencingForeignKey,
    bool IsReferencedByForeignKey,
    string? Comment);
