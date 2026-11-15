using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Gives every arrow end its own point on the border of the card it joins.
/// </summary>
/// <remarks>
/// <para>
/// An end placed where the line from card centre to card centre meets the border puts every neighbour
/// standing in the same direction on the same point, and four arrowheads on one point read as one
/// thick arrow belonging to no table in particular. So the border is dealt out instead: an end keeps
/// the side its own centre-to-centre line names for it, and the ends of one side are spread along it —
/// each keeps the place it asks for while its neighbour leaves it a mark's room, and ends asking for
/// one place are opened out around that place rather than pushed off the side they face.
/// </para>
/// <para>
/// A card's corners take no end, because a mark printed on a corner is half on the card and half on
/// the corridor. Two ends that flank one corner are a whole step apart on the walk but only about
/// twenty-five pixels apart as drawn, which is a mark's width and two gaps, so the corner stays clear
/// of the crowding the sides are dealt out to avoid. When a side is full the walk carries the surplus
/// onto the next side — the four sides are one loop — so a busy card spills above and below itself,
/// where its long sides have room, instead of queueing on a short one.
/// </para>
/// <para>
/// Order comes from the geometry and from the canonical indexes, never from the order ends were
/// collected, so the same catalog hands out the same points on every run (T5).
/// </para>
/// </remarks>
internal static class GraphEdgePorts
{
    /// <summary>The sides in the order the border is walked from the card's top-left corner.</summary>
    private static readonly EEdgeSide[] Order = [EEdgeSide.Top, EEdgeSide.Right, EEdgeSide.Bottom, EEdgeSide.Left];

    /// <summary>How far from a corner the first end of a side sits, clear of the card's rounded corner.</summary>
    private const double Inset = 8;

    /// <summary>
    /// The least room one end gets on the border. It clears the arrow head's own ink by a gap that can
    /// be seen: the head is 9 units of a 1.3-wide line, 11.7 px, and a step of 12 left the two heads a
    /// third of a pixel apart, which the picture printed as one thick mark on a card two neighbours
    /// were pointing at. Eighteen leaves six. A test reads both numbers back from the drawing and
    /// fails when the gap shrinks, because thinning this or fattening a mark silently merges the arrows
    /// again.
    /// </summary>
    internal const double MinStep = 18;

    /// <summary>Deals out the border of every card to the ends that meet it.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="placement">Where every card sits.</param>
    /// <returns>The two ends of every edge, in edge-index order.</returns>
    public static IReadOnlyList<GraphEdgeEnds> Assign(
        CatalogGraphModel model,
        IReadOnlyList<NodePlacement> placement)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(placement);

        var wanted = new List<Want>[model.Nodes.Count];
        for (var node = 0; node < wanted.Length; node++)
        {
            wanted[node] = [];
        }

        foreach (var edge in model.Edges)
        {
            var child = placement[edge.ChildIndex];
            var parent = placement[edge.ParentIndex];

            wanted[edge.ChildIndex].Add(Of(edge, child, parent, isChild: true));
            wanted[edge.ParentIndex].Add(Of(edge, parent, child, isChild: false));
        }

        var childEnds = new GraphEdgePort?[model.Edges.Count];
        var parentEnds = new GraphEdgePort?[model.Edges.Count];

        for (var node = 0; node < wanted.Length; node++)
        {
            foreach (var (want, port) in Deal(placement[node], wanted[node]))
            {
                if (want.IsChild)
                {
                    childEnds[want.EdgeIndex] = port;
                }
                else
                {
                    parentEnds[want.EdgeIndex] = port;
                }
            }
        }

        var ends = new GraphEdgeEnds[model.Edges.Count];
        for (var edge = 0; edge < ends.Length; edge++)
        {
            ends[edge] = new GraphEdgeEnds(childEnds[edge]!, parentEnds[edge]!);
        }

        return ends;
    }

    /// <summary>Hands one card's border out to the ends that meet it.</summary>
    /// <param name="node">The card.</param>
    /// <param name="wants">The ends that meet it.</param>
    /// <returns>One point per end.</returns>
    private static IEnumerable<(Want Want, GraphEdgePort Port)> Deal(NodePlacement node, List<Want> wants)
    {
        if (wants.Count == 0)
        {
            return [];
        }

        var sorted = wants
            .OrderBy(want => want.Logical)
            .ThenBy(want => want.PeerIndex)
            .ThenBy(want => want.EdgeIndex)
            .ThenByDescending(want => want.IsChild)
            .ToList();

        var usable = Usable(node);

        return sorted.Count * MinStep > usable
            ? Crowded(node, sorted, FirstAfterWidestGap(sorted, usable), usable)
            : BySide(node, sorted);
    }

    /// <summary>
    /// Deals the ends of a card that has room for all of them: an end keeps the side its own
    /// neighbour stands on, and only a side with no room for the ends that name it hands the surplus
    /// to the next side round the card. An arrow therefore leaves and arrives on a side its table
    /// faces; an end carried a whole turn round would leave in a direction the table never faces, and
    /// the only corridor open to it is the picture's own edge.
    /// </summary>
    /// <param name="node">The card.</param>
    /// <param name="sorted">The ends that meet it, in border order.</param>
    /// <returns>One point per end.</returns>
    private static List<(Want Want, GraphEdgePort Port)> BySide(
        NodePlacement node,
        IReadOnlyList<Want> sorted)
    {
        var spans = new double[Order.Length];
        var used = new int[Order.Length];
        var assigned = new List<(Want Want, double Along)>[Order.Length];

        for (var side = 0; side < Order.Length; side++)
        {
            spans[side] = Span(node, Order[side]);
            assigned[side] = [];
        }

        foreach (var want in sorted)
        {
            var side = want.Side;
            var along = want.Along;

            if (used[side] == Slots(spans[side]))
            {
                side = NextSide(used, spans, side);
                along = 0;
            }

            used[side]++;
            assigned[side].Add((want, along));
        }

        var dealt = new List<(Want Want, GraphEdgePort Port)>(sorted.Count);

        for (var side = 0; side < Order.Length; side++)
        {
            var ends = assigned[side].OrderBy(end => end.Along).ToList();
            var along = Arrange([.. ends.Select(end => end.Along)], spans[side]);

            for (var index = 0; index < ends.Count; index++)
            {
                dealt.Add((ends[index].Want, new GraphEdgePort(Order[side], At(node, Order[side], along[index]))));
            }
        }

        return dealt;
    }

    /// <summary>Spreads ends the whole border has no room for evenly round it.</summary>
    /// <param name="node">The card.</param>
    /// <param name="sorted">The ends that meet the card, in border order.</param>
    /// <param name="first">The end the walk starts at.</param>
    /// <param name="usable">The whole usable border.</param>
    /// <returns>One point per end.</returns>
    private static List<(Want Want, GraphEdgePort Port)> Crowded(
        NodePlacement node,
        IReadOnlyList<Want> sorted,
        int first,
        double usable)
    {
        var spots = Even(sorted[first].Logical, sorted.Count, usable);
        var dealt = new List<(Want Want, GraphEdgePort Port)>(sorted.Count);

        for (var step = 0; step < sorted.Count; step++)
        {
            dealt.Add((sorted[(first + step) % sorted.Count], Place(node, spots[step])));
        }

        return dealt;
    }

    /// <summary>Which side a border point stands on, and how far along that side it sits.</summary>
    /// <param name="node">The card.</param>
    /// <param name="point">The border point.</param>
    /// <returns>The side and the distance along it.</returns>
    private static (int Side, double Along) Locate(NodePlacement node, GraphPoint point)
    {
        for (var side = 0; side < Order.Length; side++)
        {
            if (OnSide(node, Order[side], point))
            {
                return (side, Math.Clamp(Along(node, Order[side], point), 0, Span(node, Order[side])));
            }
        }

        return (0, 0);
    }

    /// <summary>How many ends one side holds a mark apart, counting the first.</summary>
    /// <param name="span">The length of the side.</param>
    /// <returns>The number of ends it has room for.</returns>
    private static int Slots(double span) => Math.Max(1, (int)(span / MinStep) + 1);

    /// <summary>The first side round the card with room for one more end.</summary>
    /// <param name="used">How many ends each side has taken.</param>
    /// <param name="spans">How long each side is.</param>
    /// <param name="from">The side the end asked for.</param>
    /// <returns>The side that takes it.</returns>
    private static int NextSide(IReadOnlyList<int> used, IReadOnlyList<double> spans, int from)
    {
        for (var step = 1; step <= spans.Count; step++)
        {
            var side = (from + step) % spans.Count;
            if (used[side] < Slots(spans[side]))
            {
                return side;
            }
        }

        return from;
    }

    /// <summary>
    /// Spreads the ends of one side along it: the non-decreasing sequence that moves them least while
    /// keeping a mark between neighbours, so a run of ends that ask for one place opens out around
    /// that place instead of being pushed along the border off the side they face.
    /// </summary>
    /// <param name="alongs">Where each end asks to sit along the side, in order.</param>
    /// <param name="span">The length of the side.</param>
    /// <returns>One position along the side per end.</returns>
    private static double[] Arrange(IReadOnlyList<double> alongs, double span)
    {
        var count = alongs.Count;
        var positions = new double[count];

        if (count == 0)
        {
            return positions;
        }

        if (count == 1)
        {
            positions[0] = Math.Clamp(alongs[0], 0, span);
            return positions;
        }

        var wanted = new double[count];
        for (var index = 0; index < count; index++)
        {
            wanted[index] = alongs[index] - (index * MinStep);
        }

        var fitted = Isotonic(wanted);
        var spread = (fitted[^1] - fitted[0]) + ((count - 1) * MinStep);

        if (spread > span)
        {
            for (var index = 0; index < count; index++)
            {
                positions[index] = span * index / (count - 1);
            }

            return positions;
        }

        var shift = Math.Clamp(0, -fitted[0], span - spread - fitted[0]);

        for (var index = 0; index < count; index++)
        {
            positions[index] = fitted[index] + (index * MinStep) + shift;
        }

        return positions;
    }

    /// <summary>The non-decreasing sequence that is closest to the given values, in least squares.</summary>
    /// <param name="values">The values to fit.</param>
    /// <returns>One fitted value per input, in the same order.</returns>
    private static double[] Isotonic(IReadOnlyList<double> values)
    {
        var sums = new double[values.Count];
        var counts = new int[values.Count];
        var blocks = 0;

        foreach (var value in values)
        {
            sums[blocks] = value;
            counts[blocks] = 1;
            blocks++;

            while (blocks > 1 && (sums[blocks - 2] / counts[blocks - 2]) > (sums[blocks - 1] / counts[blocks - 1]))
            {
                sums[blocks - 2] += sums[blocks - 1];
                counts[blocks - 2] += counts[blocks - 1];
                blocks--;
            }
        }

        var fitted = new double[values.Count];
        var index = 0;

        for (var block = 0; block < blocks; block++)
        {
            var mean = sums[block] / counts[block];
            for (var step = 0; step < counts[block]; step++)
            {
                fitted[index++] = mean;
            }
        }

        return fitted;
    }

    /// <summary>Spreads ends evenly over the border, starting where the first of them faces.</summary>
    private static double[] Even(double from, int count, double usable)
    {
        var positions = new double[count];
        var step = usable / count;

        for (var index = 0; index < count; index++)
        {
            positions[index] = from + (index * step);
        }

        return positions;
    }

    /// <summary>
    /// The first end of the walk: the one after the widest empty stretch of border, so the walk runs
    /// into the gap rather than around the whole card.
    /// </summary>
    private static int FirstAfterWidestGap(IReadOnlyList<Want> sorted, double usable)
    {
        if (sorted.Count < 2)
        {
            return 0;
        }

        var widest = double.NegativeInfinity;
        var first = 0;

        for (var index = 0; index < sorted.Count; index++)
        {
            var next = (index + 1) % sorted.Count;
            var gap = sorted[next].Logical - sorted[index].Logical + (next == 0 ? usable : 0);

            if (gap > widest)
            {
                widest = gap;
                first = next;
            }
        }

        return first;
    }

    /// <summary>The border point one end faces: where its own centre-to-centre line leaves the card.</summary>
    private static GraphPoint Preferred(NodePlacement node, NodePlacement peer, CatalogGraphEdge edge, bool isChild)
    {
        // A self-reference is drawn as a loop into the corridor at the card's right, so both its ends
        // face that side: the child's above the middle, the parent's below, a mark's width apart.
        if (edge.IsSelfReference)
        {
            return new GraphPoint(node.Right, node.TopLeft.Y + (node.Height * (isChild ? 0.3 : 0.7)));
        }

        return GraphGeometry.Boundary(node, peer.Centre);
    }

    private static Want Of(CatalogGraphEdge edge, NodePlacement node, NodePlacement peer, bool isChild)
    {
        var point = Preferred(node, peer, edge, isChild);
        var (side, along) = Locate(node, point);

        return new Want(side, along, Logical(node, point), peer.NodeIndex, edge.Index, isChild);
    }

    /// <summary>
    /// Where a border point sits along the usable border, measured from the top-left corner. A point on
    /// the far end of a side is the next side's own start, so the position wraps rather than naming a
    /// place one whole turn along — a position outside the loop is read back as the loop's own start.
    /// </summary>
    private static double Logical(NodePlacement node, GraphPoint point)
    {
        var before = 0.0;

        foreach (var side in Order)
        {
            var span = Span(node, side);

            if (OnSide(node, side, point))
            {
                return (before + Math.Clamp(Along(node, side, point), 0, span)) % Usable(node);
            }

            before += span;
        }

        return 0;
    }

    /// <summary>The end that sits at one position of the usable border, with the side it sits on.</summary>
    private static GraphEdgePort Place(NodePlacement node, double logical)
    {
        var usable = Usable(node);
        var at = logical - (Math.Floor(logical / usable) * usable);

        foreach (var side in Order)
        {
            var span = Span(node, side);

            if (at < span)
            {
                return new GraphEdgePort(side, At(node, side, at));
            }

            at -= span;
        }

        return new GraphEdgePort(EEdgeSide.Left, At(node, EEdgeSide.Left, 0));
    }

    /// <summary>The length of the usable part of one side.</summary>
    private static double Span(NodePlacement node, EEdgeSide side) =>
        side is EEdgeSide.Top or EEdgeSide.Bottom ? Room(node.Width) : Room(node.Height);

    /// <summary>The whole usable border of a card: the four sides with their corners taken off.</summary>
    private static double Usable(NodePlacement node) => (2 * Room(node.Width)) + (2 * Room(node.Height));

    private static double Room(double length) => Math.Max(MinStep, length - (2 * Inset));

    /// <summary>
    /// The least width a card needs so that every end that meets it keeps a mark's room on the border.
    /// </summary>
    /// <remarks>
    /// The border is the only room an end has. The head's ink is a fixed width and two ends nearer than
    /// <see cref="MinStep"/> are printed as one thick mark, which is the merge the border is dealt out
    /// to prevent — so a card that carries more ends than its sides hold cannot be saved by dealing them
    /// better, and it cannot be saved by drawing the arrows differently either: the points themselves
    /// are what the reader counts. The card therefore widens until its two long sides hold the ends the
    /// two short ones cannot, a mark apart, and the layout reserves that width like the text's own. The
    /// short sides cannot grow — the height is what a card's two lines need and nothing more — so their
    /// share is counted first, and the long sides take the rest. A card whose text is already wider than
    /// this keeps its text's width; the caller takes the larger of the two.
    /// </remarks>
    /// <param name="ends">How many arrow ends meet the card, both directions and a self reference's two.</param>
    /// <param name="height">The height every card is drawn at.</param>
    /// <returns>The least width the border needs, which may be below the card's own minimum.</returns>
    internal static double WidthFor(int ends, double height)
    {
        var shorts = 2 * Slots(Room(height));
        var longs = Math.Max(1, (Math.Max(0, ends - shorts) + 1) / 2);
        var width = (2 * Inset) + ((longs - 1) * MinStep);

        // The ends are dealt side by side, but the card is first asked whether its whole border holds
        // them: the corners take their own share off every side, so a border whose sides each have room
        // can still fall short of one mark per end, and a card that falls short is spread evenly round
        // its rim instead — the very crowding the width exists to prevent.
        var shortest = (ends * MinStep) - (2 * Room(height));

        return Math.Max(width, (2 * Inset) + (shortest / 2));
    }

    /// <summary>The border point one end gets, rounded to the whole pixel a stroke is drawn on.</summary>
    private static GraphPoint At(NodePlacement node, EEdgeSide side, double along) => side switch
    {
        EEdgeSide.Top => new GraphPoint(Math.Round(node.TopLeft.X + Inset + along), node.TopLeft.Y),
        EEdgeSide.Right => new GraphPoint(node.Right, Math.Round(node.TopLeft.Y + Inset + along)),
        EEdgeSide.Bottom => new GraphPoint(Math.Round(node.Right - Inset - along), node.Bottom),
        _ => new GraphPoint(node.TopLeft.X, Math.Round(node.Bottom - Inset - along)),
    };

    private static double Along(NodePlacement node, EEdgeSide side, GraphPoint point) => side switch
    {
        EEdgeSide.Top => point.X - node.TopLeft.X - Inset,
        EEdgeSide.Right => point.Y - node.TopLeft.Y - Inset,
        EEdgeSide.Bottom => node.Right - point.X - Inset,
        _ => node.Bottom - point.Y - Inset,
    };

    private static bool OnSide(NodePlacement node, EEdgeSide side, GraphPoint point) => side switch
    {
        EEdgeSide.Top => Near(point.Y, node.TopLeft.Y),
        EEdgeSide.Right => Near(point.X, node.Right),
        EEdgeSide.Bottom => Near(point.Y, node.Bottom),
        _ => Near(point.X, node.TopLeft.X),
    };

    private static bool Near(double left, double right) => Math.Abs(left - right) < 0.5;

    /// <summary>One arrow end, waiting for its point on the border of the card it joins.</summary>
    /// <param name="Side">The side of the border its own neighbour stands toward.</param>
    /// <param name="Along">How far along that side it asks to sit.</param>
    /// <param name="Logical">Where it sits along the whole border, which orders the walk.</param>
    /// <param name="PeerIndex">The card at the other end of the constraint, which breaks a tie.</param>
    /// <param name="EdgeIndex">The constraint the end belongs to, which breaks the last tie.</param>
    /// <param name="IsChild">Whether the end is the storing end rather than the one pointed at.</param>
    private sealed record Want(int Side, double Along, double Logical, int PeerIndex, int EdgeIndex, bool IsChild);
}
