namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The ground the routes already cover: the x of every vertical run and the y-range drawn on it,
/// the y of every horizontal run and its x-range, and the ink of every curve and loop. A dog-leg
/// that picks a corridor is picking ground other routes may already be drawn on, and two routes
/// drawn along one line over one another are indistinguishable in the picture — one stroke, four
/// tables, and no way to say which end belongs to which constraint. Both routers keep their ground
/// here: the corridor router picks a lane, the chord router picks a way across open space, and the rule
/// that says two arrows may not be drawn on one line names the ink rather than the way it was chosen.
/// </summary>
/// <remarks>
/// <para>
/// A stretch is compared by the range it covers, never by the whole line it lies on: a corridor is
/// free where nobody has drawn in it yet, so a route may use the same x as a route far above it.
/// </para>
/// <para>
/// Every shape is a route here, because the rule names the ink and not the way the ink was chosen:
/// a curve that bows between two members of a cycle is drawn to the same border as a dog-leg, so it
/// is tested before it is drawn and claimed once it is. Leaving curves out was what let a hub
/// inside a cycle put several arrows on one stroke.
/// </para>
/// <para>
/// Two routes are one line when their strokes are drawn within <see cref="GraphEdgeRouter.InkGap"/> of one another,
/// never when their coordinates are merely equal: the parallel shift and the lane step are two
/// grids whose sums land a pixel apart, and a pixel apart is one thick stroke on the page.
/// Straight runs are held per coordinate and found by a range query; a curve's ink is bucketed by
/// cell, because a curve need not follow a corridor and the cells keep the search proportional to
/// the edge count instead of to the picture's size.
/// </para>
/// </remarks>
internal sealed class TrackUse
{
    /// <summary>How far two coordinates may differ and still be the same line.</summary>
    private const double Tolerance = 0.01;

    /// <summary>How coarse the cells that hold a curve's ink are.</summary>
    private const double Cell = 96;

    private readonly Dictionary<double, List<(double From, double To)>> _vertical = [];
    private readonly Dictionary<double, List<(double From, double To)>> _horizontal = [];
    private readonly SortedSet<double> _verticalLines = [];
    private readonly SortedSet<double> _horizontalLines = [];
    private readonly Dictionary<(int Column, int Row), List<(GraphPoint From, GraphPoint To)>> _ink = [];

    /// <summary>Whether any part of the route covers a stretch another route already covers.</summary>
    /// <param name="points">The candidate: its own points, or a curve's four control points.</param>
    /// <param name="geometry">Which of the two it is, because a curve is read between its control points.</param>
    /// <returns>Whether the route would be drawn along a stretch already in use.</returns>
    public bool IsShared(IReadOnlyList<GraphPoint> points, EEdgeGeometry geometry)
    {
        var trace = geometry == EEdgeGeometry.Polyline ? points : Sampled(points);

        for (var index = 0; index + 1 < trace.Count; index++)
        {
            if (RunsAlongALine(trace[index], trace[index + 1]))
            {
                return true;
            }

            if (RunsAlongInk(trace[index], trace[index + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records the ink a route that has been drawn now occupies.</summary>
    /// <param name="route">The route the router accepted.</param>
    public void Claim(EdgeRoute route)
    {
        if (route.Geometry != EEdgeGeometry.Polyline)
        {
            ClaimInk(Sampled(route.Points));
            return;
        }

        var points = route.Points;
        for (var index = 0; index + 1 < points.Count; index++)
        {
            var start = points[index];
            var end = points[index + 1];

            if (Math.Abs(end.X - start.X) < Tolerance)
            {
                Add(_vertical, _verticalLines, Track(start.X), Math.Min(start.Y, end.Y), Math.Max(start.Y, end.Y));
            }
            else if (Math.Abs(end.Y - start.Y) < Tolerance)
            {
                Add(_horizontal, _horizontalLines, Track(start.Y), Math.Min(start.X, end.X), Math.Max(start.X, end.X));
            }
            else
            {
                // A straight arrow drawn at an angle follows no corridor, so it is held the same way
                // a curve is. Left out, it claimed nothing and any later route could be drawn on it —
                // which is how a diagonal and a curve came to be printed as one stroke.
                ClaimInk([start, end]);
            }
        }
    }

    /// <summary>
    /// Whether the stretch runs along a corridor line another route already runs on. A stretch is
    /// read as belonging to the axis it mostly follows, and a curve's stretch counts too: what the
    /// reader sees is one line, whether a dog-leg or an arc of a cycle was laid along it.
    /// </summary>
    private bool RunsAlongALine(GraphPoint from, GraphPoint to)
    {
        var (dx, dy) = (Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));

        return dy <= dx
            ? Along(_horizontal, _horizontalLines, (from.Y + to.Y) / 2, Math.Min(from.X, to.X), Math.Max(from.X, to.X), dy <= GraphEdgeRouter.InkGap)
            : Along(_vertical, _verticalLines, (from.X + to.X) / 2, Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y), dx <= GraphEdgeRouter.InkGap);
    }

    /// <summary>Whether the stretch lies on ink a route already laid down that follows no corridor.</summary>
    private bool RunsAlongInk(GraphPoint from, GraphPoint to)
    {
        if (_ink.Count == 0)
        {
            return false;
        }

        foreach (var (column, row) in Cells(from, to, GraphEdgeRouter.InkGap))
        {
            if (!_ink.TryGetValue((column, row), out var strokes))
            {
                continue;
            }

            foreach (var (heldFrom, heldTo) in strokes)
            {
                if (Within(from, to, heldFrom, heldTo) && RunsAlong(from, to, heldFrom, heldTo))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Records ink that follows no corridor — a curve, a loop, or a straight arrow drawn at an angle — in the cells it passes through.</summary>
    private void ClaimInk(IReadOnlyList<GraphPoint> trace)
    {
        for (var index = 0; index + 1 < trace.Count; index++)
        {
            var stroke = (From: trace[index], To: trace[index + 1]);

            foreach (var (column, row) in Streak(stroke.From, stroke.To))
            {
                if (!_ink.TryGetValue((column, row), out var strokes))
                {
                    strokes = [];
                    _ink[(column, row)] = strokes;
                }

                strokes.Add(stroke);
            }
        }
    }

    /// <summary>
    /// The cells a stroke is drawn through: it is walked one column at a time, the rows it reaches
    /// inside each column, and never the cells its bounding box happens to enclose.
    /// </summary>
    /// <remarks>
    /// An angled stroke's bounding box is a field of cells while the stroke itself is a thread
    /// through them, and holding the thread in the whole field is held against every later route
    /// that passes anywhere in it. A diagonal across a wide picture filled thousands of cells, so
    /// each candidate was weighed against hundreds of strokes drawn nowhere near it — the search
    /// was correct and unusably slow on a large catalog. A cell holds only the ink that is on it.
    /// </remarks>
    /// <param name="from">Where the stroke starts.</param>
    /// <param name="to">Where it ends.</param>
    private static IEnumerable<(int Column, int Row)> Streak(GraphPoint from, GraphPoint to)
    {
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);

        if (Math.Abs(dx) < Tolerance)
        {
            var column = (int)Math.Floor(from.X / Cell);
            foreach (var row in RowsIn(Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y)))
            {
                yield return (column, row);
            }

            yield break;
        }

        if (Math.Abs(dy) < Tolerance)
        {
            var row = (int)Math.Floor(from.Y / Cell);
            foreach (var column in ColumnsIn(Math.Min(from.X, to.X), Math.Max(from.X, to.X)))
            {
                yield return (column, row);
            }

            yield break;
        }

        var (low, high) = (Math.Min(from.X, to.X), Math.Max(from.X, to.X));

        foreach (var column in ColumnsIn(low, high))
        {
            var near = from.Y + (dy * (Math.Max(low, column * Cell) - from.X) / dx);
            var far = from.Y + (dy * (Math.Min(high, (column + 1) * Cell) - from.X) / dx);

            foreach (var row in RowsIn(Math.Min(near, far), Math.Max(near, far)))
            {
                yield return (column, row);
            }
        }
    }

    /// <summary>The cell columns a span of the page touches.</summary>
    private static IEnumerable<int> ColumnsIn(double from, double to)
    {
        for (var column = (int)Math.Floor(from / Cell); column <= (int)Math.Floor(to / Cell); column++)
        {
            yield return column;
        }
    }

    /// <summary>The cell rows a span of the page touches.</summary>
    private static IEnumerable<int> RowsIn(double from, double to)
    {
        for (var row = (int)Math.Floor(from / Cell); row <= (int)Math.Floor(to / Cell); row++)
        {
            yield return row;
        }
    }

    /// <summary>
    /// Whether two stretches are near enough to be one line at all: their boxes have to meet within
    /// <see cref="GraphEdgeRouter.InkGap"/>, which is the cheapest thing a pair of strokes can be asked.
    /// </summary>
    /// <remarks>
    /// A cell holds every stroke drawn through it, and a busy cell holds many: the direction and
    /// the endpoints of each were weighed against every candidate that crossed the cell, and the
    /// picture's own cells are what the search spends most of its time on. Two stretches further
    /// apart than the gap can never be read as one stroke, so they are turned away here, where the
    /// cost is a handful of comparisons, before the angles are measured.
    /// </remarks>
    private static bool Within(GraphPoint from, GraphPoint to, GraphPoint heldFrom, GraphPoint heldTo) =>
        Math.Max(heldFrom.Y, heldTo.Y) >= Math.Min(from.Y, to.Y) - GraphEdgeRouter.InkGap
        && Math.Min(heldFrom.Y, heldTo.Y) <= Math.Max(from.Y, to.Y) + GraphEdgeRouter.InkGap
        && Math.Max(heldFrom.X, heldTo.X) >= Math.Min(from.X, to.X) - GraphEdgeRouter.InkGap
        && Math.Min(heldFrom.X, heldTo.X) <= Math.Max(from.X, to.X) + GraphEdgeRouter.InkGap;

    /// <summary>
    /// Whether two stretches are drawn along one another rather than merely meeting: they have to
    /// head the same way, and they have to lie within a stroke's gap of one another for a stretch
    /// worth drawing. A crossing meets that at one point and is left alone, which is why the
    /// direction is read first.
    /// </summary>
    /// <remarks>
    /// Two lines read as one stroke only when they run in the very same direction: a curve crossing a
    /// corridor line at a corner is steep enough to be read apart, and counting it as drawn along the
    /// line told the router that a corridor was taken when it was empty — so the routes that needed it
    /// were laid on one another instead. The shared stretch has to be worth drawing, too: a chord of a
    /// curve grazing a line for a few pixels is a crossing, not an overlap.
    /// </remarks>
    /// <remarks>
    /// What is measured is the stretch the two actually share, not the whole of the shorter one. An
    /// arrow crossing a horizontal run at a shallow angle is more than a stroke's gap from it at both
    /// of its ends, yet it lies within that gap of the run for sixty pixels in the middle — and sixty
    /// pixels drawn one on the other is one stroke however far apart the two are at their ends. That
    /// was the last shape two arrows could still merge in: the arrow that crossed was straight, the
    /// run it crossed was a lane, and both passed every other test.
    /// </remarks>
    private static bool RunsAlong(GraphPoint from, GraphPoint to, GraphPoint heldFrom, GraphPoint heldTo)
    {
        var (ux, uy) = (to.X - from.X, to.Y - from.Y);
        var (vx, vy) = (heldTo.X - heldFrom.X, heldTo.Y - heldFrom.Y);
        var cross = (ux * vy) - (uy * vx);

        // The sine of the turn between the two, compared without the square roots it is written
        // with: both sides of the test are lengths squared, which are positive either way.
        if (((ux * ux) + (uy * uy)) * ((vx * vx) + (vy * vy)) < 0.000001
            || (cross * cross) > (GraphEdgeRouter.Parallel * GraphEdgeRouter.Parallel * ((ux * ux) + (uy * uy)) * ((vx * vx) + (vy * vy))))
        {
            return false;
        }

        var (shortFrom, shortTo, longFrom, longTo) = Shorter(from, to, heldFrom, heldTo);
        var (lx, ly) = (longTo.X - longFrom.X, longTo.Y - longFrom.Y);
        var longLength = Math.Sqrt((lx * lx) + (ly * ly));

        // How far each end of the shorter stretch lies from the longer one's line, signed, so the
        // stretch of the shorter one that is inside the gap can be read off the two of them: the
        // distance runs straight from one end to the other, so the gap is met over one interval.
        var near = Distance(longFrom, lx, ly, longLength, shortFrom);
        var far = Distance(longFrom, lx, ly, longLength, shortTo);
        var (low, high) = (Math.Min(near, far), Math.Max(near, far));

        if (high < -GraphEdgeRouter.InkGap || low > GraphEdgeRouter.InkGap)
        {
            return false;
        }

        var shortLength = Math.Sqrt(((shortTo.X - shortFrom.X) * (shortTo.X - shortFrom.X))
            + ((shortTo.Y - shortFrom.Y) * (shortTo.Y - shortFrom.Y)));

        var shared = high - low < 0.000001
            ? shortLength
            : shortLength * ((Math.Min(high, GraphEdgeRouter.InkGap) - Math.Max(low, -GraphEdgeRouter.InkGap)) / (high - low));

        return shared >= GraphEdgeRouter.InkRun;
    }

    /// <summary>The two stretches ordered so the one that has to fit inside the other comes first.</summary>
    private static (GraphPoint From, GraphPoint To, GraphPoint HeldFrom, GraphPoint HeldTo) Shorter(
        GraphPoint from,
        GraphPoint to,
        GraphPoint heldFrom,
        GraphPoint heldTo)
    {
        var mine = ((to.X - from.X) * (to.X - from.X)) + ((to.Y - from.Y) * (to.Y - from.Y));
        var theirs = ((heldTo.X - heldFrom.X) * (heldTo.X - heldFrom.X)) + ((heldTo.Y - heldFrom.Y) * (heldTo.Y - heldFrom.Y));

        return mine <= theirs
            ? (from, to, heldFrom, heldTo)
            : (heldFrom, heldTo, from, to);
    }

    /// <summary>How far a point lies from the line a stretch is drawn along, signed by the side it lies on.</summary>
    private static double Distance(GraphPoint from, double ux, double uy, double length, GraphPoint point) =>
        ((ux * (point.Y - from.Y)) - (uy * (point.X - from.X))) / length;

    /// <summary>
    /// The cells a stretch touches, grown by the air two strokes need. A curve's ink need not follow
    /// a corridor, so it is found by cell rather than by coordinate.
    /// </summary>
    private static IEnumerable<(int Column, int Row)> Cells(GraphPoint from, GraphPoint to, double reach)
    {
        var firstColumn = (int)Math.Floor((Math.Min(from.X, to.X) - reach) / Cell);
        var lastColumn = (int)Math.Floor((Math.Max(from.X, to.X) + reach) / Cell);
        var firstRow = (int)Math.Floor((Math.Min(from.Y, to.Y) - reach) / Cell);
        var lastRow = (int)Math.Floor((Math.Max(from.Y, to.Y) + reach) / Cell);

        for (var column = firstColumn; column <= lastColumn; column++)
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                yield return (column, row);
            }
        }
    }

    /// <summary>A curve read as the short straight stretches the document paints for it.</summary>
    private static IReadOnlyList<GraphPoint> Sampled(IReadOnlyList<GraphPoint> points)
    {
        var trace = new List<GraphPoint>(GraphEdgeRouter.TraceSamples + 1);

        for (var sample = 0; sample <= GraphEdgeRouter.TraceSamples; sample++)
        {
            trace.Add(GraphEdgeRouter.CubicAt(points, (double)sample / GraphEdgeRouter.TraceSamples));
        }

        return trace;
    }

    /// <summary>Whether a run at this line, over this range, meets ink already drawn there.</summary>
    private static bool Along(
        Dictionary<double, List<(double From, double To)>> tracks,
        SortedSet<double> lines,
        double line,
        double from,
        double to,
        bool headsAlongAxis)
    {
        if (!headsAlongAxis)
        {
            return false;
        }

        foreach (var held in lines.GetViewBetween(line - GraphEdgeRouter.InkGap, line + GraphEdgeRouter.InkGap))
        {
            if (tracks.TryGetValue(held, out var ranges) && Covers(ranges, from, to))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether one of the ranges on a line meets the range a candidate covers on it.</summary>
    private static bool Covers(IReadOnlyList<(double From, double To)> ranges, double from, double to)
    {
        foreach (var (held, heldTo) in ranges)
        {
            if (Math.Min(to, heldTo) - Math.Max(from, held) > Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds one stretch to a line's own list, dropping the ones with no length to speak of.</summary>
    private static void Add(
        Dictionary<double, List<(double From, double To)>> tracks,
        SortedSet<double> lines,
        double line,
        double from,
        double to)
    {
        if (to - from < Tolerance)
        {
            return;
        }

        if (!tracks.TryGetValue(line, out var ranges))
        {
            ranges = [];
            tracks[line] = ranges;
            lines.Add(line);
        }

        ranges.Add((from, to));
    }

    private static double Track(double value) => Math.Round(value, 3);
}
