namespace DbClone.Application.Interchange;

/// <summary>
/// The import plan produced by the product's planner.
/// Built offline — no target connection, no DDL. Shown to the user before a restore starts.
/// </summary>
public sealed record InterchangePlanView(
    int Stages,
    int ObjectCount,
    int RefusedObjectCount,
    IReadOnlyList<string> RefusedObjects,
    IReadOnlyList<string> DependencyCycles);
