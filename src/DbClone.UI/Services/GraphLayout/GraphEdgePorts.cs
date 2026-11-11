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
/// the point its own centre-to-centre line names for it unless the end before it is nearer than a
/// mark is wide, in which case it moves on round the card. Nothing is stacked and nothing is dropped.
/// </para>
/// <para>
/// A card's corners take no end, because a mark printed on a corner is half on the card and half on
/// the corridor. When a side is full the walk carries the surplus onto the next side — the four sides
/// are one loop — so a busy card spills above and below itself, where its long sides have room,
/// instead of queueing on a short one.
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
    /// The least room one end gets on the border. It is wider than the arrow head's own ink, so two
    /// neighbouring ends never print their marks on each other; a test reads both numbers back from
    /// the drawing, because thinning this or fattening a mark silently merges the arrows again.
    /// </summary>
    internal const double MinStep = 12;

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
        var first = FirstAfterWidestGap(sorted, usable);

        var ordered = new List<Want>(sorted.Count);
        for (var step = 0; step < sorted.Count; step++)
        {
            ordered.Add(sorted[(first + step) % sorted.Count]);
        }

        var positions = LineUp(ordered, usable);
        var dealt = new List<(Want, GraphEdgePort)>(ordered.Count);

        for (var index = 0; index < ordered.Count; index++)
        {
            dealt.Add((ordered[index], Place(node, positions[index])));
        }

        return dealt;
    }

    /// <summary>
    /// The position every end gets: its own preferred one while the end before it has left room, and
    /// the first free one after it when it has not. Ends that would be pushed onto the loop's own
    /// start are spread over the whole border instead, so they stay distinct even on a card the
    /// catalog has more arrows into than the border has marks of room for.
    /// </summary>
    private static double[] LineUp(IReadOnlyList<Want> ordered, double usable)
    {
        var positions = new double[ordered.Count];
        var previous = ordered[0].Logical - MinStep;

        for (var index = 0; index < ordered.Count; index++)
        {
            previous = Math.Max(ordered[index].Logical, previous + MinStep);
            positions[index] = previous;
        }

        return previous < positions[0] + usable - MinStep
            ? positions
            : Even(ordered[0].Logical, ordered.Count, usable);
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

    private static Want Of(CatalogGraphEdge edge, NodePlacement node, NodePlacement peer, bool isChild) =>
        new(Logical(node, Preferred(node, peer, edge, isChild)), peer.NodeIndex, edge.Index, isChild);

    /// <summary>Where a border point sits along the usable border, measured from the top-left corner.</summary>
    private static double Logical(NodePlacement node, GraphPoint point)
    {
        var before = 0.0;

        foreach (var side in Order)
        {
            var span = Span(node, side);

            if (OnSide(node, side, point))
            {
                return before + Math.Clamp(Along(node, side, point), 0, span);
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
    /// <param name="Logical">Where the end would sit if the border had room for nothing else.</param>
    /// <param name="PeerIndex">The card at the other end of the constraint, which breaks a tie.</param>
    /// <param name="EdgeIndex">The constraint the end belongs to, which breaks the last tie.</param>
    /// <param name="IsChild">Whether the end is the storing end rather than the one pointed at.</param>
    private sealed record Want(double Logical, int PeerIndex, int EdgeIndex, bool IsChild);
}
