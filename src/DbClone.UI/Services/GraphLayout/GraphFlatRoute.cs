namespace DbClone.UI.Services.GraphLayout;

/// <summary>A route's flattened centerline with its bounding box, the form every hard check reads.</summary>
/// <param name="EdgeIndex">The relation the route belongs to.</param>
/// <param name="Points">The flattened centerline.</param>
/// <param name="Left">The smallest x.</param>
/// <param name="Top">The smallest y.</param>
/// <param name="Right">The largest x.</param>
/// <param name="Bottom">The largest y.</param>
/// <param name="IsChord">Whether the route is the chord between its two ports, which the cards are painted over.</param>
internal sealed record GraphFlatRoute(
    int EdgeIndex,
    IReadOnlyList<GraphPoint> Points,
    double Left,
    double Top,
    double Right,
    double Bottom,
    bool IsChord)
{
    /// <summary>Creates the flat route and measures its box.</summary>
    /// <param name="edgeIndex">The relation index.</param>
    /// <param name="points">The flattened centerline, at least one point.</param>
    /// <param name="isChord">Whether the route is the chord between its two ports.</param>
    /// <returns>The flat route.</returns>
    public static GraphFlatRoute Of(int edgeIndex, IReadOnlyList<GraphPoint> points, bool isChord = false)
    {
        var (left, top, right, bottom) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var point in points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return new GraphFlatRoute(edgeIndex, points, left, top, right, bottom, isChord);
    }

    private GraphStrokeIndex? _index;

    /// <summary>Gets the segment grid of this route, built on first use.</summary>
    public GraphStrokeIndex Index => _index ??= new GraphStrokeIndex(Points);

    /// <summary>Whether this route's box, grown by a margin, meets another's.</summary>
    /// <param name="other">The other route.</param>
    /// <param name="margin">How far to grow the box.</param>
    /// <returns>Whether the boxes meet.</returns>
    public bool Near(GraphFlatRoute other, double margin) =>
        Left - margin <= other.Right && Right + margin >= other.Left
        && Top - margin <= other.Bottom && Bottom + margin >= other.Top;
}
