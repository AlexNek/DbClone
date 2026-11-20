namespace DbClone.UI.Services.GraphLayout;

/// <summary>One failed relation and the geometry that blocks it.</summary>
/// <param name="EdgeIndex">The relation that failed.</param>
/// <param name="Kind">What failed.</param>
/// <param name="BlockingCards">The cards in the way, ascending.</param>
/// <param name="BlockingRoutes">The edge indexes of the routes in the way, ascending.</param>
/// <param name="ExploredCandidates">The candidate ordinals already tried, ascending; empty when the failure is not a routing one.</param>
internal sealed record RouteFailure(
    int EdgeIndex,
    ERouteFailureKind Kind,
    IReadOnlyList<int> BlockingCards,
    IReadOnlyList<int> BlockingRoutes,
    IReadOnlyList<int>? ExploredCandidates = null)
{
    /// <summary>Gets the candidate ordinals already tried.</summary>
    public IReadOnlyList<int> Explored => ExploredCandidates ?? [];
}
