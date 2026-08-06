using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// Represents a database object in the dependency graph.
/// </summary>
public sealed record DatabaseObject(
    string SchemaName,
    string Name,
    EDatabaseObjectType ObjectType,
    IReadOnlyList<DatabaseObjectReference> Dependencies);
