namespace DbClone.UI.Services.GraphLayout;

/// <summary>The result of dealing every card's border to the relation ends that meet it.</summary>
/// <param name="Ends">One entry per relation in edge order; null where an end found no slot.</param>
/// <param name="Failures">A <see cref="ERouteFailureKind.PortCapacity"/> failure per end that found no slot.</param>
internal sealed record GraphPortAssignment(IReadOnlyList<GraphEdgeEnds?> Ends, IReadOnlyList<RouteFailure> Failures)
{
    /// <summary>Gets whether every end has a port.</summary>
    public bool IsComplete => Failures.Count == 0;
}
