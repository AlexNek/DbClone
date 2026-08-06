namespace DbClone.Application.Models;

/// <summary>
/// Represents a check constraint.
/// </summary>
public sealed record CheckConstraintDefinition(
    string Name,
    string Expression,
    bool IsDeferrable,
    bool IsInitiallyDeferred);
