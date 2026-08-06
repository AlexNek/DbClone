namespace DbClone.Application.Models;

/// <summary>
/// Represents a domain type definition.
/// </summary>
public sealed record DomainDefinition(
    string SchemaName,
    string Name,
    string DataType,
    string? DefaultValue,
    string? CheckExpression,
    bool IsNullable,
    string? Comment);
