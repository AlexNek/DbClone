namespace DbClone.Application.Models;

/// <summary>
/// Represents an RLS policy.
/// </summary>
public sealed record PolicyDefinition(
    string SchemaName,
    string Name,
    string TableName,
    string Command,
    bool IsPermissive,
    IReadOnlyList<string> Roles,
    string? QualExpression,
    string? WithCheckExpression);
