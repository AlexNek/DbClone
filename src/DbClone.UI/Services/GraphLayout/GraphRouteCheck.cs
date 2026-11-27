namespace DbClone.UI.Services.GraphLayout;

/// <summary>The hard-validity verdict on one candidate route against the cards and the routes already reserved.</summary>
/// <param name="Flat">The flattened centerline; null when the geometry could not be trusted.</param>
/// <param name="Failures">The failures, attributed to the candidate's edge; empty when it is admissible.</param>
/// <param name="Crossings">The crossings with the reserved routes.</param>
internal sealed record GraphRouteCheck(GraphFlatRoute? Flat, IReadOnlyList<RouteFailure> Failures, IReadOnlyList<GraphCrossing> Crossings)
{
    /// <summary>Gets whether the candidate has no hard failure.</summary>
    public bool IsAdmissible => Failures.Count == 0 && Flat is not null;

    /// <summary>Gets the number of admissible crossings the candidate would add.</summary>
    public int CrossingCount => Crossings.Count(crossing => crossing.IsAdmissible);
}
