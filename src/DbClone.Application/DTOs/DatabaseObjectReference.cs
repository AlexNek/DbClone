using DbClone.Application.Enums;

namespace DbClone.Application.DTOs;

/// <summary>
/// Reference to a database object.
/// </summary>
public sealed record DatabaseObjectReference(
    string SchemaName,
    string Name,
    EDatabaseObjectType ObjectType);
