namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// How the points of a route are painted. It is a separate fact from the meaning a route carries
/// (<see cref="EEdgeShape"/>): two routes can hold the same four points and be drawn two ways, and the
/// same meaning can be drawn either way — a cycle that closes across open ground is carried by a
/// straight chord, while a plain constraint whose chord meets a card is carried by a bow.
/// </summary>
internal enum EEdgeGeometry
{
    /// <summary>Two points: one direct straight stretch between the ports.</summary>
    Straight,

    /// <summary>1 + 3n points: one or more connected cubic segments, each two controls between its ends.</summary>
    Cubic,

    /// <summary>A closed cubic sequence for a self-reference, on the Cubic encoding with the last point equal to the first.</summary>
    Loop,
}
