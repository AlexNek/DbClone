namespace DbClone.Application.Models;

/// <summary>
/// Represents an enum type definition.
/// </summary>
public sealed record EnumDefinition(
    string SchemaName,
    string Name,
    IReadOnlyList<string> Labels,
    string? Comment);
