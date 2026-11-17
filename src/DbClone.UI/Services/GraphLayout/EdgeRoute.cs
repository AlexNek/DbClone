namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// One routed edge: a shape and the points that draw it. Coordinates only — the renderer writes the
/// markup, so a routing change never touches a string and a markup change never recomputes geometry.
/// </summary>
/// <param name="EdgeIndex">The edge this route belongs to.</param>
/// <param name="Shape">Which of the three meanings the route carries.</param>
/// <param name="Geometry">
/// How the points are painted. The meaning does not answer it: a constraint between two tables is a
/// straight chord in rings and a dog-leg in a lattice, and a cycle's curve and a chord bowed aside are
/// the same four points read the same way.
/// </param>
/// <param name="Points">
/// The route's points. A polyline is two points for a straight stretch or more for a dog-leg that turns
/// in a corridor; a cubic is the two ends and the two control points between them.
/// </param>
/// <param name="LabelPoint">Where the edge's columns sit when the reader reveals them.</param>
internal sealed record EdgeRoute(
    int EdgeIndex,
    EEdgeShape Shape,
    EEdgeGeometry Geometry,
    IReadOnlyList<GraphPoint> Points,
    GraphPoint LabelPoint);
