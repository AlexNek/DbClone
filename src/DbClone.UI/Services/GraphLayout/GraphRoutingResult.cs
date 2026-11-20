namespace DbClone.UI.Services.GraphLayout;

/// <summary>The outcome of routing one placement.</summary>
/// <param name="Routes">One route per relation in edge order; null where no admissible route was found.</param>
/// <param name="Failures">One failure per unrouted relation.</param>
/// <param name="Crossings">The crossings between the accepted routes, in stable order.</param>
/// <param name="GeometryEvaluations">How many candidate routes were flattened and checked.</param>
/// <param name="BudgetExhausted">Whether the geometry-evaluation budget ended the search.</param>
internal sealed record GraphRoutingResult(
    IReadOnlyList<EdgeRoute?> Routes,
    IReadOnlyList<RouteFailure> Failures,
    IReadOnlyList<GraphCrossing> Crossings,
    int GeometryEvaluations,
    bool BudgetExhausted)
{
    /// <summary>Gets whether every relation has an admissible route.</summary>
    public bool IsComplete => Failures.Count == 0;
}
