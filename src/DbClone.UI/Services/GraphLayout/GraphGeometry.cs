using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The geometry placement and routing share: where a segment leaves a card, and whether a segment
/// passes through one. Kept in one place, so the router's clearance rule and the card's own
/// rectangle cannot drift apart.
/// </summary>
internal static class GraphGeometry
{
    /// <summary>
    /// Where the segment from the card's centre toward another point leaves the card, so an arrow
    /// touches the table instead of starting under it. Both dimensions are used: a card is wider than
    /// it is tall, and an arrow that used one half-length would start inside the border.
    /// </summary>
    /// <param name="node">The card.</param>
    /// <param name="toward">The point the segment heads for.</param>
    /// <returns>The point on the card's border.</returns>
    public static GraphPoint Boundary(NodePlacement node, GraphPoint toward)
    {
        ArgumentNullException.ThrowIfNull(node);

        var centre = node.Centre;
        var (dx, dy) = (toward.X - centre.X, toward.Y - centre.Y);

        if (dx == 0 && dy == 0)
        {
            return centre;
        }

        var scaleX = dx == 0 ? double.MaxValue : (node.Width / 2) / Math.Abs(dx);
        var scaleY = dy == 0 ? double.MaxValue : (node.Height / 2) / Math.Abs(dy);
        var scale = Math.Min(scaleX, scaleY);

        return scale == double.MaxValue
            ? centre
            : new GraphPoint(centre.X + (dx * scale), centre.Y + (dy * scale));
    }

    /// <summary>
    /// Whether a segment passes through a card, grown by the given clearance. Touching the grown
    /// border counts as a hit, which is why the two cards an edge joins are tested at a clearance of
    /// zero and only for the segments between them.
    /// </summary>
    /// <param name="start">The segment's first point.</param>
    /// <param name="end">The segment's last point.</param>
    /// <param name="card">The card to test against.</param>
    /// <param name="clearance">How far outside the card a segment must stay.</param>
    /// <returns>Whether the segment meets the grown card.</returns>
    public static bool SegmentHitsRect(GraphPoint start, GraphPoint end, NodePlacement card, double clearance)
    {
        ArgumentNullException.ThrowIfNull(card);

        var left = card.TopLeft.X - clearance;
        var top = card.TopLeft.Y - clearance;
        var right = card.Right + clearance;
        var bottom = card.Bottom + clearance;

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;

        if (dx == 0 && dy == 0)
        {
            return start.X >= left && start.X <= right && start.Y >= top && start.Y <= bottom;
        }

        var entering = 0.0;
        var leaving = 1.0;

        return Clip(-dx, start.X - left, ref entering, ref leaving)
            && Clip(dx, right - start.X, ref entering, ref leaving)
            && Clip(-dy, start.Y - top, ref entering, ref leaving)
            && Clip(dy, bottom - start.Y, ref entering, ref leaving)
            && entering <= leaving;
    }

    /// <summary>Whether a point lies inside a card, used to test a sampled curve.</summary>
    /// <param name="card">The card.</param>
    /// <param name="point">The point.</param>
    /// <returns>Whether the point is inside the card or on its border.</returns>
    public static bool Contains(NodePlacement card, GraphPoint point)
    {
        ArgumentNullException.ThrowIfNull(card);

        return point.X >= card.TopLeft.X && point.X <= card.Right
            && point.Y >= card.TopLeft.Y && point.Y <= card.Bottom;
    }

    /// <summary>
    /// How near a stretch's own end a meeting may be and still belong to the two arrows rather than to a
    /// port they were dealt beside one another.
    /// </summary>
    private const double EndShare = 0.02;

    /// <summary>
    /// Whether two stretches cross in their interiors. The ends are left out: two arrows that meet on a
    /// card's border are two constraints sharing a table, which the picture states and does not count.
    /// Both shares are read forward along their own stretch, so a pair that meets between its ends counts
    /// and a pair whose lines would meet behind both ends — two arrows running away from one another —
    /// does not.
    /// </summary>
    /// <param name="a">Where the first stretch starts.</param>
    /// <param name="b">Where the first stretch ends.</param>
    /// <param name="c">Where the second stretch starts.</param>
    /// <param name="d">Where the second stretch ends.</param>
    /// <returns>Whether the two are drawn across one another.</returns>
    public static bool SegmentsCross(GraphPoint a, GraphPoint b, GraphPoint c, GraphPoint d)
    {
        var (abx, aby) = (b.X - a.X, b.Y - a.Y);
        var (cdx, cdy) = (d.X - c.X, d.Y - c.Y);
        var denominator = (abx * cdy) - (aby * cdx);

        if (Math.Abs(denominator) < 1e-9)
        {
            return false;
        }

        var (acx, acy) = (c.X - a.X, c.Y - a.Y);
        var first = ((acx * cdy) - (acy * cdx)) / denominator;
        var second = ((acx * aby) - (acy * abx)) / denominator;

        return first > EndShare && first < 1 - EndShare && second > EndShare && second < 1 - EndShare;
    }

    /// <summary>The Liang–Barsky clip of one boundary on the segment, expressed as a parameter range.</summary>
    private static bool Clip(double direction, double distance, ref double entering, ref double leaving)
    {
        if (direction == 0)
        {
            return distance >= 0;
        }

        var crossing = distance / direction;
        if (direction < 0)
        {
            if (crossing > leaving)
            {
                return false;
            }

            entering = Math.Max(entering, crossing);
        }
        else
        {
            if (crossing < entering)
            {
                return false;
            }

            leaving = Math.Min(leaving, crossing);
        }

        return true;
    }
}
