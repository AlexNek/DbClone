namespace DbClone.Application.Models;

/// <summary>
/// Represents a table column.
/// </summary>
public sealed record ColumnDefinition(
    string Name,
    string DataType,
    int OrdinalPosition,
    bool IsNullable,
    string? DefaultValue,
    bool IsIdentity,
    bool IsGenerated,
    string? GenerationExpression,
    string? Comment,
    bool IsLocal = true);
