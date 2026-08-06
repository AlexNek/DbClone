namespace DbClone.Application.Models;

/// <summary>
/// Represents a composite type definition.
/// </summary>
public sealed record CompositeTypeDefinition(
    string SchemaName,
    string Name,
    IReadOnlyList<ColumnDefinition> Attributes,
    string? Comment);
