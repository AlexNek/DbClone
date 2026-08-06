namespace DbClone.Application.DTOs;

/// <summary>
/// Result of dependency analysis.
/// </summary>
public sealed record DependencyResult(
    IReadOnlyList<DatabaseObject> OrderedObjects,
    IReadOnlyList<CircularDependency> CircularDependencies);
