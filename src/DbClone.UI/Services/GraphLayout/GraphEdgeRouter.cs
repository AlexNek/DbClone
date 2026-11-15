using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Turns each constraint into a drawn path: a straight segment when the way is clear, a dog-leg that
/// turns in a corridor between cards when it is not, a curve when the constraint closes a cycle, and
/// a loop to the right of a table that points at itself.
/// </summary>
/// <remarks>
/// <para>
/// Two rules outrank the others. No path may cross a card it does not start or end on (plan D4 step
/// 8, T25), and no two paths may run along one line: a pair of arrows drawn on one coordinate is drawn
/// as one line, so the reader sees a single stroke joining four tables and can attribute neither end.
/// The test is the ink on the page rather than equal numbers, because two routes on separate grids
/// can land a pixel apart and be drawn as one stroke (see <see cref="InkGap"/>). Every route — a
/// dog-leg, a straight angled arrow, a cycle's curve and a self-loop alike — therefore claims the ink
/// it covers, and a candidate that would cover ink another route already covers is passed over while
/// a free line exists — in another corridor first, then on a lane beside the first. Running over
/// another route is the last resort of a picture that leaves no other way round, never the shape it
/// prefers (T26).
/// </para>
/// <para>
/// The short way is preferred among the routes that satisfy both rules — a straight segment when it
/// clears every card and every claimed stretch, otherwise the shortest clean dog-leg — so the picture
/// reads as a map of the catalog rather than as a fan of chords drawn over it.
/// </para>
/// <para>
/// An arrow is drawn to a point of the border <see cref="GraphEdgePorts"/> gave its end, and it leaves
/// that end on the outside of the side the point stands on. The rule that keeps two constraints from
/// being drawn as one line says nothing about where a line ends, and it is the end a reader uses to
/// decide which table an arrow belongs to: ends left to fall where the centre-to-centre line lands are
/// the one place two arrows can still become one. A way that ran along its own card's border would
/// then read as a stroke over the table, so every family of route starts by moving away from it.
/// </para>
/// <para>
/// A cycle is carried by the curve and nothing else, and the two directions of a mutual pair bow to
/// opposite sides, so two arrows between one pair of tables are never one line (FR13, D9).
/// </para>
/// </remarks>
internal static class GraphEdgeRouter
{
    /// <summary>How far a path stays from a card it does not touch.</summary>
    internal const double Clearance = 5;

    /// <summary>The sideways step between two constraints that join the same pair of tables.</summary>
    internal const double ParallelOffset = 22;

    /// <summary>How far a vertical run is drawn beside the corridor line it shares with the corridor's centre.</summary>
    internal const double LaneStep = 12;

    /// <summary>
    /// How far a horizontal run is drawn above or below its corridor line. Smaller than
    /// <see cref="LaneStep"/> because a row corridor is one row gap tall while a column corridor is a
    /// whole column gap wide, so the room beside the line differs by axis.
    /// </summary>
    internal const double LaneRowStep = 9;

    /// <summary>
    /// The least air two strokes need between them. A stroke is 1.3 px of ink, so two lines further
    /// apart than this read as two arrows and two lines nearer read as one thick one: the parallel
    /// shift (22) and the lane step (12) are two independent grids, so a shifted route taking a lane
    /// can land a pixel from another route's line, which is drawn as one stroke though the coordinates
    /// differ. Sharing is therefore decided by the ink on the page, not by equal numbers.
    /// </summary>
    internal const double InkGap = 6;

    /// <summary>
    /// The least angle two stretches may differ by and still be read as one line. Wider than a pixel's
    /// worth of drift and narrower than a corner: two strokes drawn on one another follow one direction,
    /// while a curve crossing a corridor line turns through a corner and is read apart.
    /// </summary>
    internal const double Parallel = 0.10;

    /// <summary>
    /// The shortest stretch two stretches can cover together and still be read as one line. Below it
    /// the contact is a crossing or a graze, which the eye reads as two arrows meeting.
    /// </summary>
    internal const double InkRun = 14;

    /// <summary>How far a closing cycle's curve bows away from the straight line between its ends.</summary>
    internal const double Bulge = 26;

    /// <summary>How far a self-reference loop reaches into the corridor beside its table.</summary>
    internal const double LoopReach = 44;

    /// <summary>How finely a curve is sampled when checking that it clears the cards.</summary>
    private const int CurveSamples = 8;

    /// <summary>How finely a curve is read back when its ink is claimed or tested against another route.</summary>
    private const int TraceSamples = 16;

    /// <summary>
    /// How many steps each ladder reaches, which is as far as a band is ever given room for.
    /// </summary>
    /// <remarks>
    /// The number is also the most runs one side of a card can be lined up one by one: a corridor yields a
    /// line per step of the ladder, and a side carrying more runs than this is a side the routing can no
    /// longer separate — two of its arrows end up drawn on one stroke however deep the band is made.
    /// Placement reads it for that reason — a fan wider than the ladder is spread over both sides of the
    /// table it reaches rather than stacked on one (see <c>GraphRankedPlacement</c>).
    /// </remarks>
    internal const int Ladder = 11;

    /// <summary>
    /// The least thickness a band needs to carry this many horizontal runs without two of them being
    /// drawn on one line.
    /// </summary>
    /// <remarks>
    /// A corridor is the room a route turns in, so a band carrying more runs than it has room for is a
    /// band that prints two arrows as one stroke — the merge the picture's first rule forbids. The count
    /// is the ends standing on the two sides that face the band: each of them has to reach its own card's
    /// border along a line of its own, and no two of those lines may coincide. The placement asks this of
    /// the lattice before any route is drawn, so the room is reserved rather than found wanting. A band
    /// is never thinner than the picture's own row gap, and never thicker than the ladders reach, because
    /// a line outside them is a line no route is ever offered.
    /// </remarks>
    /// <param name="runs">How many runs the band must carry.</param>
    /// <returns>The band's thickness.</returns>
    internal static double BandFor(int runs)
    {
        var lines = Math.Clamp(runs, 1, (2 * Ladder) + 1);

        return Math.Max(
            GraphRankedPlacement.RowGap,
            (2 * Clearance) + ((lines - 1) * LaneRowStep));
    }

    /// <summary>How far two coordinates may differ and still be the same line.</summary>
    private const double Tolerance = 0.01;

    /// <summary>Routes every edge of the graph.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="placement">Where every card sits.</param>
    /// <param name="grid">The corridors the routes may turn in.</param>
    /// <returns>One route per edge, in edge-index order.</returns>
    public static IReadOnlyList<EdgeRoute> Route(
        CatalogGraphModel model,
        IReadOnlyList<NodePlacement> placement,
        GraphGrid grid)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(grid);

        var groupOfNode = GroupOfNode(model);
        var ends = GraphEdgePorts.Assign(model, placement);
        var tracks = new TrackUse();
        var cards = new CardIndex(grid, model.Nodes.Count);
        var near = new List<int>(32);
        var routes = new List<EdgeRoute>(model.Edges.Count);

        foreach (var edge in model.Edges)
        {
            var shift = (edge.ParallelOrdinal - ((edge.ParallelCount - 1) / 2.0)) * ParallelOffset;
            var closesCycle = groupOfNode[edge.ChildIndex] >= 0
                && groupOfNode[edge.ChildIndex] == groupOfNode[edge.ParentIndex];

            var route = edge.IsSelfReference
                ? Loop(edge, placement, grid, ends[edge.Index], shift, tracks)
                : closesCycle
                    ? Cycle(edge, placement, ends[edge.Index], shift, tracks)
                    : Line(edge, placement, grid, cards, near, ends[edge.Index], shift, tracks);

            tracks.Claim(route);
            routes.Add(route);
        }

        return routes;
    }

    /// <summary>
    /// A constraint between two different parts of the picture: straight when every card and every
    /// claimed stretch is clear of it, otherwise the shortest right-angled path that turns only in
    /// empty corridors. A path that would run over another route is accepted only when no corridor and
    /// no lane of one offers a way that does not.
    /// </summary>
    private static EdgeRoute Line(
        CatalogGraphEdge edge,
        IReadOnlyList<NodePlacement> placement,
        GraphGrid grid,
        CardIndex cards,
        List<int> near,
        GraphEdgeEnds ends,
        double shift,
        TrackUse tracks)
    {
        var child = placement[edge.ChildIndex];
        var parent = placement[edge.ParentIndex];
        var start = ends.Child.Point;
        var end = ends.Parent.Point;

        var reach = Reach(child, parent, grid, shift);

        IReadOnlyList<GraphPoint>? best = null;
        var bestClears = false;
        var bestShared = true;
        var bestLength = double.MaxValue;

        void Consider(IReadOnlyList<GraphPoint> candidate)
        {
            var length = Length(candidate);

            // The winner so far clears every card and every claimed stretch, so only a way shorter than it
            // has anything left to prove: no longer candidate can take its place, whatever it clears or
            // shares, and weighing it spends the search on a route the picture will not draw.
            if (bestClears && !bestShared && length >= bestLength)
            {
                return;
            }

            var clears = Clears(candidate, placement, cards, near, edge.ChildIndex, edge.ParentIndex);

            // And a way that crosses a card cannot take the place of one that keeps off them all, so the
            // ink it would cover is not worth asking about.
            if (bestClears && !bestShared && !clears)
            {
                return;
            }

            var shared = tracks.IsShared(candidate, EEdgeShape.Line);

            if (best is null || Beats(clears, shared, length, bestClears, bestShared, bestLength))
            {
                best = candidate;
                bestClears = clears;
                bestShared = shared;
                bestLength = length;
            }
        }

        // Every corridor's own centre line is weighed, both sides of the picture alike, before a winner
        // is named. Stopping at the first way that cleared the cards and the claimed stretches is what
        // let whichever corridor answered first swallow the whole picture while an empty one on the
        // other side stayed empty.
        foreach (var candidate in Ways(child, parent, ends, grid, shift, 0, 0))
        {
            Consider(candidate);

            // The straight way is the shortest way there is between two ends, so once that one keeps off
            // every card and every claimed stretch there is nothing further to look for. A turned way is
            // not: the shorter clean turn may still be ahead, in a corridor the walk has not reached.
            if (candidate.Count == 2 && bestClears && !bestShared)
            {
                return Build(edge, candidate);
            }
        }

        // A clean way along a corridor's centre line is the answer, and the shortest of them wins. A lane
        // is the same stroke drawn a step to one side, so it is worth looking at only after the corridors
        // themselves have failed to leave the cards and the claimed stretches alone.
        if (!bestClears || bestShared)
        {
            foreach (var (laneX, laneY) in Lanes(reach))
            {
                foreach (var candidate in Ways(child, parent, ends, grid, shift, laneX, laneY))
                {
                    Consider(candidate);
                }
            }
        }

        // One lane at a time is enough while the corridor beside each card still has its own line free.
        // When both are drawn on — two cards sharing a column send their arrows down one corridor — neither
        // a step sideways alone nor a step upright alone answers, because what is wanted is the sideways
        // step for the vertical run and the upright step for the horizontal one at the same time. Those
        // are the pairs offered here, and the first pair that leaves every card and every claimed stretch
        // alone is taken: the ladder walks outwards, so the nearest clean line answers first, and the work
        // stops as soon as the arrow has a stroke of its own.
        if (bestShared)
        {
            foreach (var (laneX, laneY) in CrossingLanes(reach))
            {
                foreach (var candidate in Ways(child, parent, ends, grid, shift, laneX, laneY))
                {
                    if (Clears(candidate, placement, cards, near, edge.ChildIndex, edge.ParentIndex)
                        && !tracks.IsShared(candidate, EEdgeShape.Line))
                    {
                        return Build(edge, candidate);
                    }
                }
            }
        }

        // The ends decide which corridor families are offered, and a card's own corridors always answer,
        // so the fallback only stands in for a picture whose corridor lattice has nothing in it at all.
        return Build(edge, best ?? [start, end]);
    }

    /// <summary>
    /// The lanes a dog-leg is drawn on when every corridor's own centre line is taken: a little to one
    /// side, then a little further, so a second constraint can be drawn beside the first in the same
    /// corridor instead of along it.
    /// </summary>
    /// <remarks>
    /// A lane moves a run only sideways to itself — a vertical stretch moves in x, a horizontal one in
    /// y — and never both at once. That is the whole point: a corridor is empty along its length, so a
    /// second line beside the first is still inside it, while a line pushed the same way along the
    /// corridor's length would be pushed toward the cards the corridor runs between. An earlier version
    /// shifted every coordinate at once and could therefore only ever find ways that clipped a card,
    /// which left long routes taking the picture's own edge corridor instead.
    /// </remarks>
    /// <param name="reach">How many steps of each ladder still stand inside the room the lattice keeps.</param>
    private static IEnumerable<(double X, double Y)> Lanes((int Across, int Up) reach)
    {
        for (var step = 1; step <= reach.Across; step++)
        {
            yield return (-step * LaneStep, 0);
            yield return (step * LaneStep, 0);
        }

        for (var step = 1; step <= reach.Up; step++)
        {
            yield return (0, -step * LaneRowStep);
            yield return (0, step * LaneRowStep);
        }
    }

    /// <summary>
    /// Every sideways step paired with every upright step: the lanes to try when neither ladder alone
    /// has found a clean line, because the vertical run needs its own x and the horizontal run its own y
    /// at the same time.
    /// </summary>
    /// <remarks>
    /// The two ladders are not the same length for a reason. A column corridor is a whole column gap
    /// wide and a row corridor is one row gap tall, so a sideways step has half a corridor to use while
    /// an upright step has an eighth of one: the picture's own margins above its first row are the room
    /// the upright ladder reaches into, and a run there is what a card's top side needs.
    /// </remarks>
    /// <param name="reach">How many steps of each ladder still stand inside the room the lattice keeps.</param>
    /// <returns>The pairs, the two steps of one pair taken from different ladders.</returns>
    private static IEnumerable<(double X, double Y)> CrossingLanes((int Across, int Up) reach)
    {
        for (var upright = 1; upright <= reach.Up; upright++)
        {
            var up = upright * LaneRowStep;

            for (var sideways = 1; sideways <= reach.Across; sideways++)
            {
                var across = sideways * LaneStep;

                yield return (-across, -up);
                yield return (across, -up);
                yield return (-across, up);
                yield return (across, up);
            }
        }
    }

    /// <summary>
    /// How many steps of each ladder still stand inside the room the lattice keeps for a run beside a
    /// card: the corridor's own width, less the air a stroke owes the cards it runs between.
    /// </summary>
    /// <remarks>
    /// A lane is a stroke drawn beside a corridor's centre line, and a corridor is only as wide as the
    /// gap around it. Past that width the run is inside the row or column of cards the corridor
    /// separates, and a stroke there is a stroke over a table — <see cref="Clears"/> turns every such
    /// candidate away, so offering it only spends the search. A picture of a thousand tables spent nine
    /// tenths of its routing time on those candidates. The room is read from the lattice rather than
    /// fixed, because a hub's row is given a band deep enough to hold every run that reaches it, and a
    /// wide corridor is exactly the room those steps need.
    /// </remarks>
    /// <param name="child">The card the arrow starts at.</param>
    /// <param name="parent">The card the arrow ends on.</param>
    /// <param name="grid">The lattice the corridors belong to.</param>
    /// <param name="shift">The step a parallel set is already drawn aside by, which the lane adds to.</param>
    /// <returns>The steps that remain inside the corridors the two cards open onto.</returns>
    private static (int Across, int Up) Reach(
        NodePlacement child,
        NodePlacement parent,
        GraphGrid grid,
        double shift)
    {
        var across = 0;
        foreach (var corridor in (int[])
        [
            grid.ColumnOfNode[child.NodeIndex],
            grid.ColumnOfNode[child.NodeIndex] + 1,
            grid.ColumnOfNode[parent.NodeIndex],
            grid.ColumnOfNode[parent.NodeIndex] + 1,
        ])
        {
            across = Math.Max(across, Steps(ColumnRoom(grid, corridor) - Math.Abs(shift), LaneStep));
        }

        var up = 0;
        foreach (var corridor in (int[])
        [
            grid.RowOfNode[child.NodeIndex],
            grid.RowOfNode[child.NodeIndex] + 1,
            grid.RowOfNode[parent.NodeIndex],
            grid.RowOfNode[parent.NodeIndex] + 1,
        ])
        {
            up = Math.Max(up, Steps(RowRoom(grid, corridor) - Math.Abs(shift), LaneRowStep));
        }

        return (Math.Clamp(across, 0, Ladder), Math.Clamp(up, 0, Ladder));
    }

    /// <summary>How many whole lane steps fit in a stretch of room, once the strokes' own air is taken out.</summary>
    private static int Steps(double room, double step) =>
        (int)Math.Floor((room - Clearance) / step);

    /// <summary>How far a vertical run may be drawn either side of a column corridor's centre line.</summary>
    private static double ColumnRoom(GraphGrid grid, int corridor)
    {
        var centre = grid.ColumnCorridors[corridor];
        var left = corridor > 0 ? grid.ColumnLefts[corridor - 1] + grid.ColumnWidths[corridor - 1] : 0;
        var right = corridor < grid.ColumnLefts.Count ? grid.ColumnLefts[corridor] : grid.Width;

        return Math.Min(centre - left, right - centre);
    }

    /// <summary>How far a horizontal run may be drawn either side of a row corridor's centre line.</summary>
    private static double RowRoom(GraphGrid grid, int corridor)
    {
        var centre = grid.RowCorridors[corridor];
        var top = corridor > 0 ? grid.RowTops[corridor - 1] + GraphCard.Height : 0;
        var bottom = corridor < grid.RowTops.Count ? grid.RowTops[corridor] : grid.Height;

        return Math.Min(centre - top, bottom - centre);
    }

    /// <summary>
    /// Whether a candidate is a better drawing than the one held so far, in the order the picture
    /// cares about: a route that keeps off the cards first, then one that keeps off a stretch another
    /// route already covers, then the shorter one. A tie keeps the earlier candidate, so a straight
    /// segment wins over a dog-leg of the same length and the result never depends on iteration order.
    /// </summary>
    private static bool Beats(
        bool clears,
        bool shared,
        double length,
        bool bestClears,
        bool bestShared,
        double bestLength)
    {
        if (clears != bestClears)
        {
            return clears;
        }

        if (shared != bestShared)
        {
            return !shared;
        }

        return length < bestLength;
    }

    /// <summary>
    /// The ways from one end to the other, in the order the picture prefers them: the straight line
    /// when it leaves both cards the way their sides face, then the right-angled paths that turn only
    /// in the empty corridors beside the two cards. Which families exist is decided by the ends,
    /// because a route leaves an end on the outside of the side that end stands on — an arrow drawn
    /// along the border of its own card reads as a stroke over the table. A corridor crosses no card,
    /// so a clean way exists for every pair and the shortcut is never bought by drawing over one.
    /// </summary>
    /// <param name="child">The card the arrow starts at.</param>
    /// <param name="parent">The card the arrow ends on.</param>
    /// <param name="ends">The two points the arrow owns, one on each card.</param>
    /// <param name="grid">The lattice the corridors belong to.</param>
    /// <param name="shift">The sideways step that keeps a parallel set's routes apart.</param>
    /// <param name="laneX">How far beside the corridor's centre line the vertical runs of this pass are drawn.</param>
    /// <param name="laneY">How far beside the corridor's centre line the horizontal runs of this pass are drawn.</param>
    private static IEnumerable<IReadOnlyList<GraphPoint>> Ways(
        NodePlacement child,
        NodePlacement parent,
        GraphEdgeEnds ends,
        GraphGrid grid,
        double shift,
        double laneX,
        double laneY)
    {
        var from = ends.Child;
        var to = ends.Parent;
        var start = from.Point;
        var end = to.Point;

        if (Leaves(from, end) && Leaves(to, start))
        {
            yield return [start, end];
        }

        var xs = ColumnCorridors(grid, child, parent, shift, laneX);
        var ys = RowCorridors(grid, child, parent, shift, laneY);

        foreach (var way in (from.LeavesSideways, to.LeavesSideways) switch
        {
            (true, true) => SidewaysWays(from, to, xs, ys),
            (true, false) => OutSidewaysWays(from, to, xs, ys),
            (false, true) => InSidewaysWays(from, to, xs, ys),
            _ => UprightWays(from, to, xs, ys),
        })
        {
            yield return way;
        }
    }

    /// <summary>
    /// The ways between two ends that both leave their card sideways: out to a column corridor, along
    /// it, and back into the other card. When a third card stands in that corridor too, the way goes
    /// out to one column, across a row corridor, along it past the blocker, back across and in — the
    /// two cards' own borders are then the only lines the arrow runs beside, and it runs on neither.
    /// </summary>
    private static IEnumerable<IReadOnlyList<GraphPoint>> SidewaysWays(
        GraphEdgePort from,
        GraphEdgePort to,
        List<double> xs,
        List<double> ys)
    {
        var start = from.Point;
        var end = to.Point;

        if (Math.Abs(start.Y - end.Y) > Tolerance)
        {
            foreach (var x in xs.Where(value => LeavesX(from, value) && LeavesX(to, value)))
            {
                yield return [start, new GraphPoint(x, start.Y), new GraphPoint(x, end.Y), end];
            }
        }

        foreach (var y in ys.Where(value => Math.Abs(value - start.Y) > Tolerance && Math.Abs(value - end.Y) > Tolerance))
        {
            foreach (var outX in xs.Where(value => LeavesX(from, value)))
            {
                foreach (var inX in xs.Where(value => LeavesX(to, value) && Math.Abs(value - outX) > Tolerance))
                {
                    yield return
                    [
                        start,
                        new GraphPoint(outX, start.Y),
                        new GraphPoint(outX, y),
                        new GraphPoint(inX, y),
                        new GraphPoint(inX, end.Y),
                        end,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// The ways between two ends that both leave their card upright: out to a row corridor, along it,
    /// and back down into the other card. A card standing between the two — the usual case in one
    /// column of tables, where the arrow's own column is the column the blocker occupies — sends the
    /// way around in x, because the only way past a card in the line the ends share is out of it.
    /// </summary>
    private static IEnumerable<IReadOnlyList<GraphPoint>> UprightWays(
        GraphEdgePort from,
        GraphEdgePort to,
        List<double> xs,
        List<double> ys)
    {
        var start = from.Point;
        var end = to.Point;

        if (Math.Abs(start.X - end.X) > Tolerance)
        {
            foreach (var y in ys.Where(value => LeavesY(from, value) && LeavesY(to, value)))
            {
                yield return [start, new GraphPoint(start.X, y), new GraphPoint(end.X, y), end];
            }
        }

        foreach (var x in xs.Where(value => Math.Abs(value - start.X) > Tolerance && Math.Abs(value - end.X) > Tolerance))
        {
            foreach (var outY in ys.Where(value => LeavesY(from, value)))
            {
                foreach (var inY in ys.Where(value => LeavesY(to, value) && Math.Abs(value - outY) > Tolerance))
                {
                    yield return
                    [
                        start,
                        new GraphPoint(start.X, outY),
                        new GraphPoint(x, outY),
                        new GraphPoint(x, inY),
                        new GraphPoint(end.X, inY),
                        end,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// The ways out of one card sideways and into the other upright: the one turn where the two
    /// approach lines cross, then the pair of corridors that carries the way when a card stands on
    /// that crossing.
    /// </summary>
    private static IEnumerable<IReadOnlyList<GraphPoint>> OutSidewaysWays(
        GraphEdgePort from,
        GraphEdgePort to,
        List<double> xs,
        List<double> ys)
    {
        var start = from.Point;
        var end = to.Point;

        if (LeavesX(from, end.X) && LeavesY(to, start.Y))
        {
            yield return [start, new GraphPoint(end.X, start.Y), end];
        }

        foreach (var x in xs.Where(value => LeavesX(from, value) && Math.Abs(value - end.X) > Tolerance))
        {
            foreach (var y in ys.Where(value => LeavesY(to, value) && Math.Abs(value - start.Y) > Tolerance))
            {
                yield return
                [
                    start,
                    new GraphPoint(x, start.Y),
                    new GraphPoint(x, y),
                    new GraphPoint(end.X, y),
                    end,
                ];
            }
        }
    }

    /// <summary>The ways out of one card upright and into the other sideways: the mirror of the above.</summary>
    private static IEnumerable<IReadOnlyList<GraphPoint>> InSidewaysWays(
        GraphEdgePort from,
        GraphEdgePort to,
        List<double> xs,
        List<double> ys)
    {
        var start = from.Point;
        var end = to.Point;

        if (LeavesY(from, end.Y) && LeavesX(to, start.X))
        {
            yield return [start, new GraphPoint(start.X, end.Y), end];
        }

        foreach (var y in ys.Where(value => LeavesY(from, value) && Math.Abs(value - end.Y) > Tolerance))
        {
            foreach (var x in xs.Where(value => LeavesX(to, value) && Math.Abs(value - start.X) > Tolerance))
            {
                yield return
                [
                    start,
                    new GraphPoint(start.X, y),
                    new GraphPoint(x, y),
                    new GraphPoint(x, end.Y),
                    end,
                ];
            }
        }
    }

    /// <summary>
    /// The corridor lines a vertical run may be drawn along: the one on either side of each of the two
    /// cards. Four of them, so the search stays proportional to the edge count rather than to the
    /// picture's size.
    /// </summary>
    private static List<double> ColumnCorridors(
        GraphGrid grid,
        NodePlacement child,
        NodePlacement parent,
        double shift,
        double laneX)
    {
        var lines = new List<double>(4);

        foreach (var column in (int[])
        [
            grid.ColumnOfNode[child.NodeIndex],
            grid.ColumnOfNode[child.NodeIndex] + 1,
            grid.ColumnOfNode[parent.NodeIndex],
            grid.ColumnOfNode[parent.NodeIndex] + 1,
        ])
        {
            var x = grid.ColumnCorridors[column] + shift + laneX;
            if (!lines.Any(value => Math.Abs(value - x) < 0.5))
            {
                lines.Add(x);
            }
        }

        return lines;
    }

    /// <summary>The corridor lines a horizontal run may be drawn along: the one above and below each card.</summary>
    private static List<double> RowCorridors(
        GraphGrid grid,
        NodePlacement child,
        NodePlacement parent,
        double shift,
        double laneY)
    {
        var lines = new List<double>(4);

        foreach (var row in (int[])
        [
            grid.RowOfNode[child.NodeIndex],
            grid.RowOfNode[child.NodeIndex] + 1,
            grid.RowOfNode[parent.NodeIndex],
            grid.RowOfNode[parent.NodeIndex] + 1,
        ])
        {
            var y = grid.RowCorridors[row] + shift + laneY;
            if (!lines.Any(value => Math.Abs(value - y) < 0.5))
            {
                lines.Add(y);
            }
        }

        return lines;
    }

    /// <summary>Whether the way to a point leaves an end on the side it stands on rather than along it.</summary>
    private static bool Leaves(GraphEdgePort port, GraphPoint toward) =>
        port.LeavesSideways ? LeavesX(port, toward.X) : LeavesY(port, toward.Y);

    /// <summary>Whether a run drawn along this x leaves a sideways end on the outside of its card.</summary>
    private static bool LeavesX(GraphEdgePort port, double x) =>
        (port.OutwardX * (x - port.Point.X)) > Tolerance;

    /// <summary>Whether a run drawn along this y leaves an upright end on the outside of its card.</summary>
    private static bool LeavesY(GraphEdgePort port, double y) =>
        (port.OutwardY * (y - port.Point.Y)) > Tolerance;

    /// <summary>
    /// A constraint whose two ends sit in one cyclic group, drawn as a curve so the picture states
    /// the cycle. The two directions of a mutual pair bow to opposite sides; a bulge that would cross
    /// a card is flipped, then reduced, and a bulge drawn along a route already in the picture is
    /// passed over for the next one, so a hub inside a cycle does not put its arrows on one stroke.
    /// </summary>
    private static EdgeRoute Cycle(
        CatalogGraphEdge edge,
        IReadOnlyList<NodePlacement> placement,
        GraphEdgeEnds ends,
        double shift,
        TrackUse tracks)
    {
        var start = ends.Child.Point;
        var end = ends.Parent.Point;

        var (dx, dy) = (end.X - start.X, end.Y - start.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var (normalX, normalY) = length > 0 ? (-dy / length, dx / length) : (0.0, 1.0);
        var side = edge.ChildIndex < edge.ParentIndex ? -1 : 1;

        IReadOnlyList<GraphPoint>? first = null;
        IReadOnlyList<GraphPoint>? firstClear = null;

        foreach (var bulge in CycleBulges(shift))
        {
            foreach (var signed in new[] { side, -side })
            {
                var points = Curve(start, end, normalX * signed * bulge, normalY * signed * bulge);
                first ??= points;

                if (!CurveClears(points, placement))
                {
                    continue;
                }

                firstClear ??= points;

                if (!tracks.IsShared(points, EEdgeShape.Detour))
                {
                    return new EdgeRoute(edge.Index, EEdgeShape.Detour, points, Midpoint(points));
                }
            }
        }

        // Every arc crosses a card or lies on an arrow already drawn: the picture keeps the cycle and
        // takes the overlap, which is what the rule asks for when no other way exists (T26).
        var accepted = firstClear ?? first!;
        return new EdgeRoute(edge.Index, EEdgeShape.Detour, accepted, Midpoint(accepted));
    }

    /// <summary>
    /// The bulges a closing cycle is tried at: a gentle bow first, then wider arcs, because two
    /// members of one block may not be neighbours and the curve has to go around the cards between
    /// them instead of across them.
    /// </summary>
    private static IEnumerable<double> CycleBulges(double shift)
    {
        yield return Bulge + shift;
        yield return Bulge * 2;
        yield return Bulge * 3.5;
        yield return Bulge * 5.5;
        yield return Bulge * 8;
    }

    /// <summary>
    /// A table pointing at itself: a loop drawn into the corridor at its right, so the arrow says
    /// "itself" without running across the card or over its neighbour. Both of its ends belong to the
    /// one card, so each is given its own point on it: two self-references drawn into one point are
    /// drawn as one arrow naming neither constraint. A reach whose outer leg would fall on an arrow
    /// already drawn gives way to a shorter one, which stays inside the same corridor.
    /// </summary>
    private static EdgeRoute Loop(
        CatalogGraphEdge edge,
        IReadOnlyList<NodePlacement> placement,
        GraphGrid grid,
        GraphEdgeEnds ends,
        double shift,
        TrackUse tracks)
    {
        var node = placement[edge.ChildIndex];
        var column = grid.ColumnOfNode[node.NodeIndex];
        var limit = column + 1 < grid.ColumnLefts.Count
            ? grid.ColumnLefts[column + 1]
            : grid.Width - (GraphRankedPlacement.Margin / 2);

        var available = Math.Max(0, limit - node.Right);
        var reach = Math.Clamp((available / 2) + shift, 14, LoopReach);

        var start = ends.Child;
        var end = ends.Parent;

        // The loop bows out of each end along the side that end stands on, which for the ends a
        // self-reference is given is the card's own right — the corridor the reach is measured into.
        var shortest = Math.Max(12, reach * 0.7);
        IReadOnlyList<GraphPoint>? accepted = null;

        foreach (var length in new[] { reach, shortest })
        {
            IReadOnlyList<GraphPoint> points =
                [start.Point, Out(start, length), Out(end, length), end.Point];
            accepted ??= points;

            if (!tracks.IsShared(points, EEdgeShape.Loop))
            {
                accepted = points;
                reach = length;
                break;
            }
        }

        return new EdgeRoute(
            edge.Index,
            EEdgeShape.Loop,
            accepted!,
            new GraphPoint(node.Right + reach + 8, node.Centre.Y));
    }

    /// <summary>The point a route reaches once it has left an end by the given distance.</summary>
    private static GraphPoint Out(GraphEdgePort port, double distance) =>
        new(port.Point.X + (port.OutwardX * distance), port.Point.Y + (port.OutwardY * distance));

    /// <summary>
    /// Whether a route keeps clear of every card. The card an end stands on is met by design — the end's
    /// point is on its border — so it is passed over, but only for the segment that end owns: the other
    /// card is tested even on the first and last segments, because a way that reached its end by crossing
    /// the other table would be drawn as a stroke over that table.
    /// </summary>
    /// <param name="points">The candidate route.</param>
    /// <param name="placement">Where every card sits.</param>
    /// <param name="cards">The cards by cell, so only the ones a segment reaches are tested.</param>
    /// <param name="near">The scratch list <paramref name="cards"/> fills; reused so a search allocates nothing.</param>
    /// <param name="childIndex">The card the arrow starts at.</param>
    /// <param name="parentIndex">The card the arrow ends on.</param>
    private static bool Clears(
        IReadOnlyList<GraphPoint> points,
        IReadOnlyList<NodePlacement> placement,
        CardIndex cards,
        List<int> near,
        int childIndex,
        int parentIndex)
    {
        for (var segment = 0; segment + 1 < points.Count; segment++)
        {
            var first = segment == 0;
            var last = segment + 2 == points.Count;

            cards.Collect(near, points[segment], points[segment + 1], Clearance);

            foreach (var node in near)
            {
                var endpoint = node == childIndex || node == parentIndex;

                // A card a segment starts or ends on is met by design: the arrow's own end stands on its
                // border. Any other contact is a stroke over a table, the two cards the route joins
                // included — that is how a way that climbed past its own child came to be drawn on it.
                if (endpoint
                    && (GraphGeometry.Contains(placement[node], points[segment])
                        || GraphGeometry.Contains(placement[node], points[segment + 1])))
                {
                    continue;
                }

                var clearance = endpoint ? 0 : Clearance;

                if (GraphGeometry.SegmentHitsRect(points[segment], points[segment + 1], placement[node], clearance))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a sampled curve stays off every card, its own two included.</summary>
    private static bool CurveClears(IReadOnlyList<GraphPoint> points, IReadOnlyList<NodePlacement> placement)
    {
        for (var sample = 1; sample < CurveSamples; sample++)
        {
            var t = (double)sample / CurveSamples;
            var position = CubicAt(points, t);

            foreach (var card in placement)
            {
                if (GraphGeometry.Contains(card, position))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<GraphPoint> Curve(
        GraphPoint start,
        GraphPoint end,
        double offsetX,
        double offsetY)
    {
        var thirdX = (end.X - start.X) / 3;
        var thirdY = (end.Y - start.Y) / 3;

        return
        [
            start,
            new GraphPoint(start.X + thirdX + offsetX, start.Y + thirdY + offsetY),
            new GraphPoint(start.X + (2 * thirdX) + offsetX, start.Y + (2 * thirdY) + offsetY),
            end,
        ];
    }

    private static GraphPoint CubicAt(IReadOnlyList<GraphPoint> points, double t)
    {
        var inverse = 1 - t;
        var weightStart = inverse * inverse * inverse;
        var weightOne = 3 * inverse * inverse * t;
        var weightTwo = 3 * inverse * t * t;
        var weightEnd = t * t * t;

        return new GraphPoint(
            (weightStart * points[0].X) + (weightOne * points[1].X) + (weightTwo * points[2].X) + (weightEnd * points[3].X),
            (weightStart * points[0].Y) + (weightOne * points[1].Y) + (weightTwo * points[2].Y) + (weightEnd * points[3].Y));
    }

    private static EdgeRoute Build(CatalogGraphEdge edge, IReadOnlyList<GraphPoint> points) =>
        new(edge.Index, EEdgeShape.Line, points, Midpoint(points));

    private static double Length(IReadOnlyList<GraphPoint> points)
    {
        var length = 0.0;
        for (var index = 0; index + 1 < points.Count; index++)
        {
            var (dx, dy) = (points[index + 1].X - points[index].X, points[index + 1].Y - points[index].Y);
            length += Math.Sqrt((dx * dx) + (dy * dy));
        }

        return length;
    }

    /// <summary>The point halfway along the drawn path, which is where the edge's hidden column text sits.</summary>
    private static GraphPoint Midpoint(IReadOnlyList<GraphPoint> points)
    {
        if (points.Count < 2)
        {
            return points[0];
        }

        if (points.Count == 4)
        {
            return new GraphPoint(
                (points[0].X + (3 * points[1].X) + (3 * points[2].X) + points[3].X) / 8,
                (points[0].Y + (3 * points[1].Y) + (3 * points[2].Y) + points[3].Y) / 8);
        }

        var half = Length(points) / 2;
        var walked = 0.0;
        for (var index = 0; index + 1 < points.Count; index++)
        {
            var (dx, dy) = (points[index + 1].X - points[index].X, points[index + 1].Y - points[index].Y);
            var segment = Math.Sqrt((dx * dx) + (dy * dy));
            if (walked + segment >= half && segment > 0)
            {
                var share = (half - walked) / segment;
                return new GraphPoint(points[index].X + (dx * share), points[index].Y + (dy * share));
            }

            walked += segment;
        }

        return points[^1];
    }

    /// <summary>The cyclic group each node belongs to, or -1 for a table outside every cycle.</summary>
    private static int[] GroupOfNode(CatalogGraphModel model)
    {
        var groupOfNode = new int[model.Nodes.Count];
        Array.Fill(groupOfNode, -1);

        foreach (var group in model.CyclicGroups)
        {
            foreach (var node in group.MemberNodeIndexes)
            {
                groupOfNode[node] = group.Number;
            }
        }

        return groupOfNode;
    }

    /// <summary>
    /// The cards arranged by the cell each one fills, so a segment is tested only against the cards it
    /// can reach.
    /// </summary>
    /// <remarks>
    /// A card fills exactly one cell of the placement lattice — one per column, one per row — so the
    /// lattice doubles as a bucket grid and no bucketing arithmetic is needed. A segment grown by the
    /// clearance names the columns and rows that could be within reach, and only those cells are
    /// visited; a vertical run in a corridor between two columns names none at all. Without this the
    /// lane search would test every candidate against every card, which is quadratic in the picture's
    /// size and made a thousand-table catalog take a minute to route.
    /// </remarks>
    private sealed class CardIndex
    {
        private readonly int _columns;
        private readonly int _rows;
        private readonly int[] _nodeOfCell;
        private readonly double[] _lefts;
        private readonly double[] _rights;
        private readonly double[] _tops;
        private readonly double[] _bottoms;

        /// <summary>Indexes the cards by the cell of the lattice they occupy.</summary>
        /// <param name="grid">The lattice the cards were placed on.</param>
        /// <param name="count">How many nodes there are.</param>
        public CardIndex(GraphGrid grid, int count)
        {
            ArgumentNullException.ThrowIfNull(grid);

            _columns = Math.Max(1, grid.ColumnLefts.Count);
            _rows = Math.Max(1, grid.RowTops.Count);
            _nodeOfCell = new int[_columns * _rows];
            Array.Fill(_nodeOfCell, -1);

            for (var node = 0; node < count; node++)
            {
                _nodeOfCell[(grid.RowOfNode[node] * _columns) + grid.ColumnOfNode[node]] = node;
            }

            _lefts = [.. grid.ColumnLefts];
            _rights = [.. grid.ColumnWidths.Select((width, column) => grid.ColumnLefts[column] + width)];
            _tops = [.. grid.RowTops];
            _bottoms = [.. grid.RowTops.Select(top => top + GraphCard.Height)];
        }

        /// <summary>
        /// Fills a list with the cards a segment could meet, its ends grown by the clearance. The list
        /// is emptied first so the caller may keep one for the whole search.
        /// </summary>
        /// <param name="into">The list to fill.</param>
        /// <param name="start">Where the segment starts.</param>
        /// <param name="end">Where the segment ends.</param>
        /// <param name="clearance">How far from a card the segment must stay.</param>
        public void Collect(List<int> into, GraphPoint start, GraphPoint end, double clearance)
        {
            into.Clear();

            var left = Math.Min(start.X, end.X) - clearance;
            var right = Math.Max(start.X, end.X) + clearance;
            var top = Math.Min(start.Y, end.Y) - clearance;
            var bottom = Math.Max(start.Y, end.Y) + clearance;

            // The columns march left to right and the rows top to bottom, so the cells in reach are one
            // contiguous block of the lattice: find its two corners and visit nothing outside them.
            var firstColumn = 0;
            while (firstColumn < _columns && _rights[firstColumn] < left)
            {
                firstColumn++;
            }

            var lastColumn = _columns - 1;
            while (lastColumn >= firstColumn && _lefts[lastColumn] > right)
            {
                lastColumn--;
            }

            var firstRow = 0;
            while (firstRow < _rows && _bottoms[firstRow] < top)
            {
                firstRow++;
            }

            var lastRow = _rows - 1;
            while (lastRow >= firstRow && _tops[lastRow] > bottom)
            {
                lastRow--;
            }

            for (var row = firstRow; row <= lastRow; row++)
            {
                var line = row * _columns;
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    var node = _nodeOfCell[line + column];
                    if (node >= 0)
                    {
                        into.Add(node);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The ground the routes already cover: the x of every vertical run and the y-range drawn on it,
    /// the y of every horizontal run and its x-range, and the ink of every curve and loop. A dog-leg
    /// that picks a corridor is picking ground other routes may already be drawn on, and two routes
    /// drawn along one line over one another are indistinguishable in the picture — one stroke, four
    /// tables, and no way to say which end belongs to which constraint.
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
    /// Two routes are one line when their strokes are drawn within <see cref="InkGap"/> of one another,
    /// never when their coordinates are merely equal: the parallel shift and the lane step are two
    /// grids whose sums land a pixel apart, and a pixel apart is one thick stroke on the page.
    /// Straight runs are held per coordinate and found by a range query; a curve's ink is bucketed by
    /// cell, because a curve need not follow a corridor and the cells keep the search proportional to
    /// the edge count instead of to the picture's size.
    /// </para>
    /// </remarks>
    private sealed class TrackUse
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
        /// <param name="shape">Which of the two it is, because a curve is read between its control points.</param>
        /// <returns>Whether the route would be drawn along a stretch already in use.</returns>
        public bool IsShared(IReadOnlyList<GraphPoint> points, EEdgeShape shape)
        {
            var trace = shape == EEdgeShape.Line ? points : Sampled(points);

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
            if (route.Shape != EEdgeShape.Line)
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
                ? Along(_horizontal, _horizontalLines, (from.Y + to.Y) / 2, Math.Min(from.X, to.X), Math.Max(from.X, to.X), dy <= InkGap)
                : Along(_vertical, _verticalLines, (from.X + to.X) / 2, Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y), dx <= InkGap);
        }

        /// <summary>Whether the stretch lies on ink a route already laid down that follows no corridor.</summary>
        private bool RunsAlongInk(GraphPoint from, GraphPoint to)
        {
            if (_ink.Count == 0)
            {
                return false;
            }

            foreach (var (column, row) in Cells(from, to, InkGap))
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
        /// <see cref="InkGap"/>, which is the cheapest thing a pair of strokes can be asked.
        /// </summary>
        /// <remarks>
        /// A cell holds every stroke drawn through it, and a busy cell holds many: the direction and
        /// the endpoints of each were weighed against every candidate that crossed the cell, and the
        /// picture's own cells are what the search spends most of its time on. Two stretches further
        /// apart than the gap can never be read as one stroke, so they are turned away here, where the
        /// cost is a handful of comparisons, before the angles are measured.
        /// </remarks>
        private static bool Within(GraphPoint from, GraphPoint to, GraphPoint heldFrom, GraphPoint heldTo) =>
            Math.Max(heldFrom.Y, heldTo.Y) >= Math.Min(from.Y, to.Y) - InkGap
            && Math.Min(heldFrom.Y, heldTo.Y) <= Math.Max(from.Y, to.Y) + InkGap
            && Math.Max(heldFrom.X, heldTo.X) >= Math.Min(from.X, to.X) - InkGap
            && Math.Min(heldFrom.X, heldTo.X) <= Math.Max(from.X, to.X) + InkGap;

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
                || (cross * cross) > (Parallel * Parallel * ((ux * ux) + (uy * uy)) * ((vx * vx) + (vy * vy))))
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

            if (high < -InkGap || low > InkGap)
            {
                return false;
            }

            var shortLength = Math.Sqrt(((shortTo.X - shortFrom.X) * (shortTo.X - shortFrom.X))
                + ((shortTo.Y - shortFrom.Y) * (shortTo.Y - shortFrom.Y)));

            var shared = high - low < 0.000001
                ? shortLength
                : shortLength * ((Math.Min(high, InkGap) - Math.Max(low, -InkGap)) / (high - low));

            return shared >= InkRun;
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
            var trace = new List<GraphPoint>(TraceSamples + 1);

            for (var sample = 0; sample <= TraceSamples; sample++)
            {
                trace.Add(CubicAt(points, (double)sample / TraceSamples));
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

            foreach (var held in lines.GetViewBetween(line - InkGap, line + InkGap))
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
}
