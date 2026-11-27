namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// What a trial re-route needs to know about the evaluation it replaces: the placement state, cards,
/// ports and routes the previous evaluation committed, so a move can re-route only what it invalidated.
/// </summary>
/// <param name="Before">The placement state of the previous evaluation, in its own frame.</param>
/// <param name="Nodes">The cards of the previous evaluation, as they were drawn.</param>
/// <param name="Ends">The ports of the previous evaluation, one per relation.</param>
/// <param name="Routes">The routes of the previous evaluation; null for a relation it could not route.</param>
internal sealed record GraphRouteWarmup(
    GraphPlacementState Before,
    IReadOnlyList<NodePlacement> Nodes,
    IReadOnlyList<GraphEdgeEnds?> Ends,
    IReadOnlyList<EdgeRoute?> Routes);
