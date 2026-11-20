namespace DbClone.UI.Services.GraphLayout;

/// <summary>The best valid layout over every seed, or the explicit reasons there is none.</summary>
/// <param name="Best">
/// The chosen layout; null only when no seed produced a valid one or the caller cancelled. The watchdog never
/// empties it: a search it cut short still answers with the cheapest complete layout it could reach.
/// </param>
/// <param name="Reports">One report per seed, in seed order.</param>
/// <param name="GeometryEvaluations">The evaluations spent over all seeds.</param>
/// <param name="WasCancelled">Whether the caller's token ended the request; such a request returns no layout.</param>
/// <param name="WasTimedOut">Whether the elapsed-time watchdog cut the search short, whatever it returned.</param>
/// <param name="IsRough">
/// Whether the layout is the arrangement of last resort: no seed passed the hard rules, so the whole graph is
/// drawn from port to port without the promise that a stroke clears a card. Such a picture is always a
/// warning beside a picture, never a silent substitute for a valid one.
/// </param>
internal sealed record GraphSelectionOutcome(
    GraphLayoutCandidate? Best,
    IReadOnlyList<GraphSeedReport> Reports,
    int GeometryEvaluations,
    bool WasCancelled,
    bool WasTimedOut,
    bool IsRough = false)
{
    /// <summary>Gets whether a valid layout was chosen.</summary>
    public bool IsSuccess => Best is not null;

    /// <summary>
    /// Gets whether the layout is usable but was not the one the search would have chosen: the watchdog stopped
    /// the refining, so it is drawn with the crossings and the reading order that seed happened to give.
    /// </summary>
    public bool IsDegraded => WasTimedOut && Best is not null;
}
