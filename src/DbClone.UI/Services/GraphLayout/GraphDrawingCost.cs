using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// What one finished picture costs to read: how many arrows are drawn across a card they do not join,
/// how many along ground another arrow already covers, how many turn in a corridor, how many point back
/// up the page, how many meet in the open, and how far the ink travels in all.
/// </summary>
/// <remarks>
/// <para>
/// The two arrangements draw the same catalog on different ground, so neither can be judged by the rules
/// it follows internally: a lattice spends corridors and keeps its reading order, rings spend space and
/// keep every arrow straight. The picture that is drawn is the one that measures cheaper, counted on the
/// geometry both of them actually produced — the same stretches the document is about to paint — so the
/// comparison says nothing either arrangement cannot be seen to do (plan D12).
/// </para>
/// <para>
/// The weights state which of the reader's complaints is the louder one. An arrow drawn over a card, or a
/// second arrow drawn on the stroke of the first, is the fact the picture promises never to do while
/// another way exists, so each costs a whole picture's worth. A turn is what the reader sees as a
/// rectangle path and asks not to be given, and it outweighs the bands' own rule that a table stand under
/// the table it names: a hierarchy whose arrows all go straight down keeps the bands, and one that needs
/// a corridor is drawn in rings instead. Length is counted last and lightly, in card heights, so a
/// picture's cost does not depend on how large its cards are drawn.
/// </para>
/// <para>
/// Everything is a plain function of the coordinates, so the same catalog settles the same way on every
/// run and under any culture. Nothing here decides an arrangement: it measures the two that were placed.
/// </para>
/// </remarks>
internal readonly record struct GraphDrawingCost
{
    /// <summary>What one arrow drawn across a card it does not join costs.</summary>
    private const double BlockedWeight = 1000;

    /// <summary>What one arrow drawn along ground another arrow already covers costs.</summary>
    private const double SharedWeight = 1000;

    /// <summary>What one turn of one arrow, the rectangle the reader sees, costs.</summary>
    private const double TurnWeight = 60;

    /// <summary>What one arrow standing the wrong way up — a child drawn above its parent — costs.</summary>
    private const double WrongWayWeight = 20;

    /// <summary>What one pair of arrows meeting in the open costs.</summary>
    private const double CrossingWeight = 6;

    /// <summary>How many arrow pairs are compared before the count is taken as a proxy.</summary>
    private const int ComparisonBudget = 250_000;

    /// <summary>How finely one curve is walked into the stretches the reader actually sees.</summary>
    private const int CurveSteps = 12;

    /// <summary>Gets how many arrows are drawn across a card they do not join.</summary>
    internal int Blocked { get; init; }

    /// <summary>Gets how many arrows are drawn along ink another arrow already covers.</summary>
    internal int Shared { get; init; }

    /// <summary>Gets how many turns every arrow of the picture takes together.</summary>
    internal int Turns { get; init; }

    /// <summary>Gets how many relations stand with their child above their parent.</summary>
    internal int WrongWay { get; init; }

    /// <summary>Gets how many pairs of arrows cross one another.</summary>
    internal int Crossings { get; init; }

    /// <summary>Gets how far the picture's ink travels, in card heights.</summary>
    internal double Length { get; init; }

    /// <summary>Gets the one number the two arrangements are compared by.</summary>
    internal double Total =>
        (Blocked + Shared) * BlockedWeight
        + Turns * TurnWeight
        + WrongWay * WrongWayWeight
        + Crossings * CrossingWeight
        + Length;

    /// <summary>Measures the picture as it was placed and routed.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="nodes">Where every card sits.</param>
    /// <param name="routes">How every constraint is drawn.</param>
    /// <returns>The cost of reading that picture.</returns>
    internal static GraphDrawingCost Of(
        CatalogGraphModel model,
        IReadOnlyList<NodePlacement> nodes,
        IReadOnlyList<EdgeRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(routes);

        var lines = new List<GraphPoint>[model.Edges.Count];
        foreach (var route in routes)
        {
            lines[route.EdgeIndex] = Trace(route);
        }

        var blocked = 0;
        var turns = 0;
        var wrongWay = 0;
        var length = 0d;

        foreach (var edge in model.Edges)
        {
            var line = lines[edge.Index];
            length += LengthOf(line);
            turns += TurnsOf(line);

            if (edge.IsSelfReference)
            {
                continue;
            }

            if (nodes[edge.ChildIndex].TopLeft.Y < nodes[edge.ParentIndex].TopLeft.Y)
            {
                wrongWay++;
            }

            if (CrossesACard(line, nodes, edge))
            {
                blocked++;
            }
        }

        return new GraphDrawingCost
        {
            Blocked = blocked,
            Shared = SharedInk(routes),
            Turns = turns,
            WrongWay = wrongWay,
            Crossings = CrossingCount(model, lines),
            Length = length / GraphCard.Height,
        };
    }

    /// <summary>
    /// Whether a candidate is the better picture. A tie keeps the arrangement that stands, so a catalog
    /// already drawn cleanly is never rearranged by the order the candidates happened to be computed in.
    /// </summary>
    internal static bool MeasuresBetterThan(GraphDrawingCost candidate, GraphDrawingCost standing) =>
        candidate.Total < standing.Total;

    /// <summary>Whether any stretch of the route is drawn across a card it does not join.</summary>
    private static bool CrossesACard(
        List<GraphPoint> line,
        IReadOnlyList<NodePlacement> nodes,
        CatalogGraphEdge edge)
    {
        var box = Bounds(line);

        for (var card = 0; card < nodes.Count; card++)
        {
            if (card == edge.ChildIndex || card == edge.ParentIndex)
            {
                continue;
            }

            // A card outside the box the route covers is never in its way, and the box test is what keeps
            // the count linear in the cards a route can actually reach.
            if (nodes[card].Right < box.left || nodes[card].TopLeft.X > box.right
                || nodes[card].Bottom < box.top || nodes[card].TopLeft.Y > box.bottom)
            {
                continue;
            }

            foreach (var (start, end) in Stretches(line))
            {
                if (GraphGeometry.SegmentHitsRect(start, end, nodes[card], 2))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// How many turns the picture takes: a route of more than two points bends once at every point
    /// between its ends, and a curve is not a bend — the reader sees a bowed line, not a rectangle.
    /// </summary>
    private static int TurnsOf(List<GraphPoint> line)
    {
        var turns = 0;

        for (var i = 1; i + 1 < line.Count; i++)
        {
            var (beforeX, beforeY) = (line[i].X - line[i - 1].X, line[i].Y - line[i - 1].Y);
            var (afterX, afterY) = (line[i + 1].X - line[i].X, line[i + 1].Y - line[i].Y);

            if (Math.Sign(beforeX) != Math.Sign(afterX) || Math.Sign(beforeY) != Math.Sign(afterY))
            {
                turns++;
            }
        }

        return turns;
    }

    /// <summary>How many arrows are drawn along ink an earlier arrow already claimed.</summary>
    private static int SharedInk(IReadOnlyList<EdgeRoute> routes)
    {
        var tracks = new TrackUse();
        var shared = 0;

        foreach (var route in routes)
        {
            if (tracks.IsShared(route.Points, route.Geometry))
            {
                shared++;
            }

            tracks.Claim(route);
        }

        return shared;
    }

    /// <summary>How many pairs of arrows that share no table meet in the open.</summary>
    private static int CrossingCount(CatalogGraphModel model, List<GraphPoint>[] lines)
    {
        var pairs = 0;
        var crossings = 0;

        for (var a = 0; a < model.Edges.Count; a++)
        {
            for (var b = a + 1; b < model.Edges.Count; b++)
            {
                if (pairs >= ComparisonBudget)
                {
                    // A picture too large to compare in full is judged on the proxy the bound reached:
                    // the rate the pairs before it crossed at, carried over the pairs left uncounted.
                    return (int)(crossings + (double)(model.Edges.Count * (model.Edges.Count - 1) / 2 - pairs)
                        * crossings / Math.Max(1, pairs));
                }

                pairs++;

                if (SharesTable(model.Edges[a], model.Edges[b]))
                {
                    continue;
                }

                if (AnyStretchCrosses(lines[a], lines[b]))
                {
                    crossings++;
                }
            }
        }

        return crossings;
    }

    private static bool SharesTable(CatalogGraphEdge a, CatalogGraphEdge b) =>
        a.ChildIndex == b.ChildIndex || a.ChildIndex == b.ParentIndex
        || a.ParentIndex == b.ChildIndex || a.ParentIndex == b.ParentIndex;

    private static bool AnyStretchCrosses(List<GraphPoint> first, List<GraphPoint> second)
    {
        foreach (var (oneStart, oneEnd) in Stretches(first))
        {
            foreach (var (twoStart, twoEnd) in Stretches(second))
            {
                if (Intersect(oneStart, oneEnd, twoStart, twoEnd))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether two stretches of the picture are drawn across one another, read by the one rule the chord
    /// router itself bows a way to avoid — so what is counted and what is avoided cannot drift apart.
    /// </summary>
    private static bool Intersect(GraphPoint a, GraphPoint b, GraphPoint c, GraphPoint d) =>
        GraphGeometry.SegmentsCross(a, b, c, d);

    /// <summary>
    /// The route read as the stretches the document paints: a cubic is walked between its controls, and a
    /// polyline is taken as its corners. The two cannot be told apart by how many points a route carries —
    /// a dog-leg and a bowed chord are both four points, and reading the one as the other would measure
    /// the diagonal a corridor never draws instead of the two turns it does.
    /// </summary>
    private static List<GraphPoint> Trace(EdgeRoute route)
    {
        var points = route.Points;
        if (route.Geometry == EEdgeGeometry.Polyline)
        {
            return [.. points];
        }

        var walked = new List<GraphPoint> { points[0] };
        for (var step = 1; step <= CurveSteps; step++)
        {
            walked.Add(Cubic(points, step / (double)CurveSteps));
        }

        return walked;
    }

    private static GraphPoint Cubic(IReadOnlyList<GraphPoint> points, double t)
    {
        var u = 1 - t;

        return new GraphPoint(
            u * u * u * points[0].X + 3 * u * u * t * points[1].X + 3 * u * t * t * points[2].X + t * t * t * points[3].X,
            u * u * u * points[0].Y + 3 * u * u * t * points[1].Y + 3 * u * t * t * points[2].Y + t * t * t * points[3].Y);
    }

    private static IEnumerable<(GraphPoint Start, GraphPoint End)> Stretches(List<GraphPoint> line)
    {
        for (var i = 0; i + 1 < line.Count; i++)
        {
            yield return (line[i], line[i + 1]);
        }
    }

    private static double LengthOf(List<GraphPoint> line)
    {
        var length = 0d;

        foreach (var (start, end) in Stretches(line))
        {
            length += Math.Sqrt(((end.X - start.X) * (end.X - start.X)) + ((end.Y - start.Y) * (end.Y - start.Y)));
        }

        return length;
    }

    private static (double left, double top, double right, double bottom) Bounds(List<GraphPoint> line)
    {
        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;

        foreach (var point in line)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return (left, top, right, bottom);
    }
}
