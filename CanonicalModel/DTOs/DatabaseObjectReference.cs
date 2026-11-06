using CanonicalModel.Enums;

namespace CanonicalModel.DTOs;

/// <summary>
/// Reference to a database object.
/// </summary>
public sealed record DatabaseObjectReference(
    string SchemaName,
    string Name,
    EDatabaseObjectType ObjectType);
