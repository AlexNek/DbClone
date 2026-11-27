namespace DbClone.UI.Services.GraphLayout;

/// <summary>A complete, valid layout: every card placed, every relation routed, nothing overlapping.</summary>
/// <param name="Name">The seed it came from.</param>
/// <param name="Layout">The placement and canvas.</param>
/// <param name="Ends">The border ports of every relation.</param>
/// <param name="Routes">One route per relation in edge order.</param>
/// <param name="Crossings">The crossings between the routes.</param>
/// <param name="GeometryEvaluations">The evaluations spent reaching this layout.</param>
/// <param name="Moves">The placement moves repair took.</param>
/// <param name="Expansions">The canvas expansions repair took.</param>
internal sealed record GraphLayoutCandidate(
    string Name,
    GraphDraftLayout Layout,
    IReadOnlyList<GraphEdgeEnds> Ends,
    IReadOnlyList<EdgeRoute> Routes,
    IReadOnlyList<GraphCrossing> Crossings,
    int GeometryEvaluations,
    int Moves,
    int Expansions)
{
    /// <summary>Gets the number of crossings the hard rules admit; the score's first term.</summary>
    public int CrossingCount => Crossings.Count(crossing => crossing.IsAdmissible);
}
