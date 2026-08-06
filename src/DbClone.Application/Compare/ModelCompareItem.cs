using DbClone.Application.Enums;

namespace DbClone.Application.Compare;

/// <summary>
/// A single comparison result produced by an <see cref="IModelComparer"/>.
/// Represents presence or definition differences for one database object.
/// </summary>
public sealed record ModelCompareItem(
    EDatabaseObjectType ObjectType,
    string SchemaName,
    string ObjectName,
    ECompareStatus Status,
    string Details);
