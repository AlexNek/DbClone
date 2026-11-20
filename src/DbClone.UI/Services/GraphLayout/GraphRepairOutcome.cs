namespace DbClone.UI.Services.GraphLayout;

/// <summary>Either a complete valid layout or the explicit reasons repair could not produce one.</summary>
/// <param name="Candidate">The layout; null on failure.</param>
/// <param name="Failures">The route and port failures left; empty on success.</param>
/// <param name="ExhaustedBudgets">The names of the budgets that ended the search; empty on success.</param>
/// <param name="GeometryEvaluations">The evaluations spent.</param>
/// <param name="WasCancelled">Whether cancellation ended the search.</param>
internal sealed record GraphRepairOutcome(
    GraphLayoutCandidate? Candidate,
    IReadOnlyList<RouteFailure> Failures,
    IReadOnlyList<string> ExhaustedBudgets,
    int GeometryEvaluations,
    bool WasCancelled)
{
    /// <summary>Gets whether repair produced a complete valid layout.</summary>
    public bool IsSuccess => Candidate is not null;
}
