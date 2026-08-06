namespace DbClone.Application.Models;

/// <summary>
/// Represents a materialized view definition.
/// </summary>
public sealed record MaterializedViewDefinition(
    string SchemaName,
    string Name,
    string Definition,
    string? Tablespace,
    IReadOnlyList<string> Columns,
    string? Comment);
