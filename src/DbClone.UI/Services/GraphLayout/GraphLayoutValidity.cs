namespace DbClone.UI.Services.GraphLayout;

/// <summary>The hard-validity report of one candidate layout.</summary>
internal sealed class GraphLayoutValidity
{
    /// <summary>Creates the report.</summary>
    /// <param name="failures">The failures, in edge-index then kind order.</param>
    /// <param name="crossings">The crossings, in stable pair then coordinate order.</param>
    public GraphLayoutValidity(IReadOnlyList<RouteFailure> failures, IReadOnlyList<GraphCrossing> crossings)
    {
        Failures = failures ?? throw new ArgumentNullException(nameof(failures));
        Crossings = crossings ?? throw new ArgumentNullException(nameof(crossings));
    }

    /// <summary>Gets the hard failures. A layout with any is rejected.</summary>
    public IReadOnlyList<RouteFailure> Failures { get; }

    /// <summary>Gets every crossing found, admissible or not.</summary>
    public IReadOnlyList<GraphCrossing> Crossings { get; }

    /// <summary>Gets whether the layout has no hard failure.</summary>
    public bool IsValid => Failures.Count == 0;

    /// <summary>Gets the number of admissible crossings, the score's crossing count.</summary>
    public int AdmissibleCrossingCount => Crossings.Count(crossing => crossing.IsAdmissible);
}
