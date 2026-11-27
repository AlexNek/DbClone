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
    /// Whether a segment passes through a card, grown by the given clearance. Touching the grown border counts
    /// as a hit. A negative clearance shrinks the card instead, which is how the two cards a relation joins are
    /// held to their border: their port lies on it, so the stroke has to touch them.
    /// </summary>
    /// <param name="start">The segment's first point.</param>
    /// <param name="end">The segment's last point.</param>
    /// <param name="card">The card to test against.</param>
    /// <param name="clearance">How far outside the card a segment must stay; negative to shrink it.</param>
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

    /// <summary>Evaluates one cubic Bézier at a parameter.</summary>
    /// <param name="p0">The start point.</param>
    /// <param name="p1">The first control.</param>
    /// <param name="p2">The second control.</param>
    /// <param name="p3">The end point.</param>
    /// <param name="t">The parameter, 0 at the start and 1 at the end.</param>
    /// <returns>The point on the curve.</returns>
    public static GraphPoint CubicAt(GraphPoint p0, GraphPoint p1, GraphPoint p2, GraphPoint p3, double t)
    {
        var u = 1 - t;
        var (a, b, c, d) = (u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t);

        return new GraphPoint(
            (a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X),
            (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y));
    }

    /// <summary>
    /// Flattens a connected cubic sequence (<c>1 + 3n</c> points) adaptively: a segment is split until
    /// its control points lie within <see cref="GraphGeometryContract.CurveFlatnessTolerance"/> of its
    /// chord. A segment that still exceeds the tolerance at the depth bound makes the result incomplete.
    /// </summary>
    /// <param name="points">The cubic sequence's points.</param>
    /// <returns>The flattened points and whether they are trustworthy.</returns>
    public static GraphFlatCurve Flatten(IReadOnlyList<GraphPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 4 || (points.Count - 1) % 3 != 0)
        {
            throw new ArgumentException("A cubic sequence holds 1 + 3n points.", nameof(points));
        }

        var flat = new List<GraphPoint> { points[0] };
        var complete = true;

        for (var at = 0; at + 3 < points.Count; at += 3)
        {
            complete &= Subdivide(points[at], points[at + 1], points[at + 2], points[at + 3], 0, flat);
        }

        return new GraphFlatCurve(flat, complete);
    }

    /// <summary>The distance from a point to a segment.</summary>
    /// <param name="point">The point.</param>
    /// <param name="start">The segment's first point.</param>
    /// <param name="end">The segment's last point.</param>
    /// <returns>The shortest distance.</returns>
    public static double DistanceToSegment(GraphPoint point, GraphPoint start, GraphPoint end)
    {
        var (dx, dy) = (end.X - start.X, end.Y - start.Y);
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared == 0)
        {
            return Distance(point, start);
        }

        var t = Math.Clamp((((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared, 0, 1);

        return Distance(point, new GraphPoint(start.X + (t * dx), start.Y + (t * dy)));
    }

    /// <summary>The shortest distance between two segments, zero when they meet.</summary>
    /// <param name="a">The first segment's start.</param>
    /// <param name="b">The first segment's end.</param>
    /// <param name="c">The second segment's start.</param>
    /// <param name="d">The second segment's end.</param>
    /// <returns>The minimum distance.</returns>
    public static double SegmentDistance(GraphPoint a, GraphPoint b, GraphPoint c, GraphPoint d)
    {
        if (TryIntersection(a, b, c, d, out _))
        {
            return 0;
        }

        return Math.Min(
            Math.Min(DistanceToSegment(a, c, d), DistanceToSegment(b, c, d)),
            Math.Min(DistanceToSegment(c, a, b), DistanceToSegment(d, a, b)));
    }

    /// <summary>
    /// Whether two segments meet at one point, closed at both ends. Parallel and collinear segments
    /// never report a point: a shared run is classified by distance, not by intersection.
    /// </summary>
    /// <param name="a">The first segment's start.</param>
    /// <param name="b">The first segment's end.</param>
    /// <param name="c">The second segment's start.</param>
    /// <param name="d">The second segment's end.</param>
    /// <param name="point">The meeting point.</param>
    /// <returns>Whether a single intersection point exists.</returns>
    public static bool TryIntersection(GraphPoint a, GraphPoint b, GraphPoint c, GraphPoint d, out GraphPoint point)
    {
        var (abx, aby) = (b.X - a.X, b.Y - a.Y);
        var (cdx, cdy) = (d.X - c.X, d.Y - c.Y);
        var denominator = (abx * cdy) - (aby * cdx);
        point = default;

        if (Math.Abs(denominator) < 1e-12)
        {
            return false;
        }

        var (acx, acy) = (c.X - a.X, c.Y - a.Y);
        var first = ((acx * cdy) - (acy * cdx)) / denominator;
        var second = ((acx * aby) - (acy * abx)) / denominator;

        if (first < 0 || first > 1 || second < 0 || second > 1)
        {
            return false;
        }

        point = new GraphPoint(a.X + (first * abx), a.Y + (first * aby));

        return true;
    }

    /// <summary>
    /// The acute angle, in radians, between two segments' directions: 0 when parallel, a quarter turn
    /// when perpendicular.
    /// </summary>
    /// <param name="a">The first segment's start.</param>
    /// <param name="b">The first segment's end.</param>
    /// <param name="c">The second segment's start.</param>
    /// <param name="d">The second segment's end.</param>
    /// <returns>The angle in <c>[0, π/2]</c>.</returns>
    public static double CrossingAngle(GraphPoint a, GraphPoint b, GraphPoint c, GraphPoint d)
    {
        var (abx, aby) = (b.X - a.X, b.Y - a.Y);
        var (cdx, cdy) = (d.X - c.X, d.Y - c.Y);
        var lengths = Math.Sqrt(((abx * abx) + (aby * aby)) * ((cdx * cdx) + (cdy * cdy)));
        if (lengths == 0)
        {
            return 0;
        }

        var cosine = Math.Clamp(Math.Abs((abx * cdx) + (aby * cdy)) / lengths, 0, 1);

        return Math.Acos(cosine);
    }

    /// <summary>The length of a flattened curve.</summary>
    /// <param name="curve">The flattened points.</param>
    /// <returns>The summed segment lengths.</returns>
    public static double ArcLength(IReadOnlyList<GraphPoint> curve)
    {
        ArgumentNullException.ThrowIfNull(curve);

        var length = 0.0;
        for (var index = 1; index < curve.Count; index++)
        {
            length += Distance(curve[index - 1], curve[index]);
        }

        return length;
    }

    /// <summary>The point at a fraction of a flattened curve's arclength; 0.5 is the arclength midpoint.</summary>
    /// <param name="curve">The flattened points, at least one.</param>
    /// <param name="fraction">The fraction along the curve, from 0 to 1.</param>
    /// <returns>The point on the curve.</returns>
    public static GraphPoint ArcPoint(IReadOnlyList<GraphPoint> curve, double fraction)
    {
        ArgumentNullException.ThrowIfNull(curve);

        var target = ArcLength(curve) * Math.Clamp(fraction, 0, 1);
        var walked = 0.0;

        for (var index = 1; index < curve.Count; index++)
        {
            var step = Distance(curve[index - 1], curve[index]);
            if (step > 0 && walked + step >= target)
            {
                var t = (target - walked) / step;
                return new GraphPoint(
                    curve[index - 1].X + (t * (curve[index].X - curve[index - 1].X)),
                    curve[index - 1].Y + (t * (curve[index].Y - curve[index - 1].Y)));
            }

            walked += step;
        }

        return curve[^1];
    }

    /// <summary>
    /// Whether a flattened segment or its visible stroke envelope enters a card, the card being grown by
    /// <see cref="GraphGeometryContract.CardCenterlineClearance"/>. A caller testing a card the relation joins
    /// passes the clearance it needs instead, because its port sits on that card's border.
    /// </summary>
    /// <param name="start">The segment's first point.</param>
    /// <param name="end">The segment's last point.</param>
    /// <param name="card">The card to test.</param>
    /// <returns>Whether the segment violates the card's clearance.</returns>
    public static bool SegmentHitsCardEnvelope(GraphPoint start, GraphPoint end, NodePlacement card) =>
        SegmentHitsRect(start, end, card, GraphGeometryContract.CardCenterlineClearance);

    /// <summary>The distance between two points.</summary>
    /// <param name="first">The first point.</param>
    /// <param name="second">The second point.</param>
    /// <returns>The straight-line distance.</returns>
    public static double Distance(GraphPoint first, GraphPoint second) =>
        Math.Sqrt(Math.Pow(second.X - first.X, 2) + Math.Pow(second.Y - first.Y, 2));

    /// <summary>Splits one cubic until flat; returns false when the depth bound ended a still-curved piece.</summary>
    private static bool Subdivide(GraphPoint p0, GraphPoint p1, GraphPoint p2, GraphPoint p3, int depth, List<GraphPoint> flat)
    {
        var deviation = Math.Max(DistanceToSegment(p1, p0, p3), DistanceToSegment(p2, p0, p3));
        if (deviation <= GraphGeometryContract.CurveFlatnessTolerance)
        {
            flat.Add(p3);
            return true;
        }

        if (depth >= GraphGeometryContract.MaximumCurveSubdivisionDepth)
        {
            flat.Add(p3);
            return false;
        }

        var p01 = Mid(p0, p1);
        var p12 = Mid(p1, p2);
        var p23 = Mid(p2, p3);
        var p012 = Mid(p01, p12);
        var p123 = Mid(p12, p23);
        var middle = Mid(p012, p123);

        var left = Subdivide(p0, p01, p012, middle, depth + 1, flat);
        var right = Subdivide(middle, p123, p23, p3, depth + 1, flat);

        return left && right;
    }

    private static GraphPoint Mid(GraphPoint first, GraphPoint second) =>
        new((first.X + second.X) / 2, (first.Y + second.Y) / 2);

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
