namespace DbClone.UI.Services.GraphLayout;

/// <summary>Where one node's card sits, and how big it is.</summary>
/// <param name="NodeIndex">The node this placement belongs to.</param>
/// <param name="TopLeft">The card's top-left corner.</param>
/// <param name="Width">The card's width, measured from the two lines printed inside it.</param>
/// <param name="Height">The card's height, which every card shares.</param>
internal sealed record NodePlacement(
    int NodeIndex,
    GraphPoint TopLeft,
    double Width,
    double Height)
{
    /// <summary>Gets the card's centre, which is where an edge attaches before clipping.</summary>
    public GraphPoint Centre => new(TopLeft.X + (Width / 2), TopLeft.Y + (Height / 2));

    /// <summary>Gets the card's right edge.</summary>
    public double Right => TopLeft.X + Width;

    /// <summary>Gets the card's bottom edge.</summary>
    public double Bottom => TopLeft.Y + Height;
}
