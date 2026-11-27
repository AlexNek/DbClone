namespace DbClone.UI.Services.GraphLayout;

/// <summary>What happened to one seed of one connected component.</summary>
/// <param name="Name">The seed's name.</param>
/// <param name="Score">The score when the seed produced a valid layout.</param>
/// <param name="Failures">The route and port failures when it did not, in whole-graph edge and node indexes.</param>
/// <param name="ExhaustedBudgets">The budgets that ended the seed's search.</param>
/// <param name="GeometryEvaluations">The evaluations the seed spent.</param>
/// <param name="Component">The connected component the seed laid out.</param>
internal sealed record GraphSeedReport(
    string Name,
    GraphLayoutScore? Score,
    IReadOnlyList<RouteFailure> Failures,
    IReadOnlyList<string> ExhaustedBudgets,
    int GeometryEvaluations,
    int Component = 0)
{
    /// <summary>Gets whether the seed produced a complete valid layout.</summary>
    public bool IsValid => Score is not null;

    /// <summary>
    /// Gets whether this component's contest took this seed: the report of the winner, flagged by the
    /// selector with the same comparison it chose by. The facts count clustered wins by this, not by
    /// validity — a valid seed that lost is not a drawn cluster.
    /// </summary>
    public bool Won { get; init; }
}
