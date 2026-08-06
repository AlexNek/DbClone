namespace DbClone.Application.Models;

/// <summary>
/// Represents a foreign key.
/// </summary>
public sealed record ForeignKeyDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string UpdateRule,
    string DeleteRule,
    bool IsDeferrable,
    bool IsInitiallyDeferred);
