namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// How the points of a route are painted. It is a separate fact from the meaning a route carries
/// (<see cref="EEdgeShape"/>): two routes can hold the same four points and be drawn two ways, and the
/// same meaning can be drawn either way — a cycle that closes across open ground is carried by a
/// straight chord, while a plain constraint whose chord meets a card is carried by a bow.
/// </summary>
internal enum EEdgeGeometry
{
    /// <summary>The points are joined one to the next by a straight stretch: a chord or a dog-leg.</summary>
    Polyline,

    /// <summary>The first and last points are the ends and the middle two are a cubic curve's controls.</summary>
    Cubic,
}
