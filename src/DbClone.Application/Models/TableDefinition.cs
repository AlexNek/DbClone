namespace DbClone.Application.Models;

/// <summary>
/// Represents a table definition.
/// </summary>
public sealed record TableDefinition(
    string SchemaName,
    string Name,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<IndexDefinition> Indexes,
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<CheckConstraintDefinition> CheckConstraints,
    IReadOnlyList<UniqueConstraintDefinition> UniqueConstraints,
    string? Comment,
    bool IsPartitioned,
    string? PartitionStrategy,
    string? ParentTable,
    string? PartitionBound = null);
