namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The one point on a card's border that belongs to one arrow end. The route is drawn to it and never
/// to a point that fell into place: two constraints whose ends share a border point are drawn as one
/// arrow, and the reader can attribute neither end.
/// </summary>
/// <param name="Side">The side the point sits on, which is the way out the route has to take.</param>
/// <param name="Point">The point on the card's border.</param>
internal sealed record GraphEdgePort(EEdgeSide Side, GraphPoint Point)
{
    /// <summary>Gets the direction, in x, the route travels as it leaves this end.</summary>
    public int OutwardX => Side switch
    {
        EEdgeSide.Left => -1,
        EEdgeSide.Right => 1,
        _ => 0,
    };

    /// <summary>Gets the direction, in y, the route travels as it leaves this end.</summary>
    public int OutwardY => Side switch
    {
        EEdgeSide.Top => -1,
        EEdgeSide.Bottom => 1,
        _ => 0,
    };

    /// <summary>Gets whether the first segment of the route is horizontal rather than vertical.</summary>
    public bool LeavesSideways => OutwardX != 0;
}
