namespace DbClone.UI.Services.GraphLayout;

/// <summary>One placement, fully ports-assigned, routed and validated. It is never shown unless it is complete.</summary>
/// <param name="Layout">The assembled placement.</param>
/// <param name="Ports">The border ports.</param>
/// <param name="Routing">The routes after rip-up and reroute.</param>
/// <param name="Crossings">The crossings between the accepted routes.</param>
/// <param name="Objective">The repair ranking.</param>
internal sealed record GraphRepairEvaluation(
    GraphDraftLayout Layout,
    GraphPortAssignment Ports,
    GraphRoutingResult Routing,
    IReadOnlyList<GraphCrossing> Crossings,
    GraphRepairObjective Objective)
{
    /// <summary>Gets whether every end has a port and every relation a valid route.</summary>
    public bool IsComplete => Objective.IsZero;
}
