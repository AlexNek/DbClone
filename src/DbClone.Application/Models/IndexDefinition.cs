namespace DbClone.Application.Models;

/// <summary>
/// Represents an index.
/// </summary>
public sealed record IndexDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    bool IsPrimary,
    string? FilterExpression,
    string? Tablespace,
    string? Definition = null,
    bool IsConstraint = false);
