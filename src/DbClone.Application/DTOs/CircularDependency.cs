namespace DbClone.Application.DTOs;

/// <summary>
/// Represents a circular dependency.
/// </summary>
public sealed record CircularDependency(
    IReadOnlyList<DatabaseObjectReference> Cycle,
    string Description);
