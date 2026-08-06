namespace DbClone.Application.Models;

/// <summary>
/// Represents a trigger definition.
/// </summary>
public sealed record TriggerDefinition(
    string SchemaName,
    string Name,
    string TableName,
    string Timing,
    IReadOnlyList<string> Events,
    string FunctionSchema,
    string FunctionName,
    bool IsRowLevel,
    bool IsEnabled,
    string? Condition,
    string? Comment);
