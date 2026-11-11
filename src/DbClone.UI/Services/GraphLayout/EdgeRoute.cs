namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// One routed edge: a shape and the points that draw it. Coordinates only — the renderer writes the
/// markup, so a routing change never touches a string and a markup change never recomputes geometry.
/// </summary>
/// <param name="EdgeIndex">The edge this route belongs to.</param>
/// <param name="Shape">Which of the three meanings the route carries.</param>
/// <param name="Points">
/// The route's points. A line is two points for a straight segment or more for a dog-leg that turns
/// in a corridor; a detour and a loop are the four control points of a cubic curve.
/// </param>
/// <param name="LabelPoint">Where the edge's columns sit when the reader reveals them.</param>
internal sealed record EdgeRoute(
    int EdgeIndex,
    EEdgeShape Shape,
    IReadOnlyList<GraphPoint> Points,
    GraphPoint LabelPoint);
