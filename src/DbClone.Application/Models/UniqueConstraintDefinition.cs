namespace DbClone.Application.Models;

/// <summary>
/// Represents a unique constraint.
/// </summary>
public sealed record UniqueConstraintDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsDeferrable,
    bool IsInitiallyDeferred);
