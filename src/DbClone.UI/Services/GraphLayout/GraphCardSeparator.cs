namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Moves overlapping cards apart along their minimum separating axis. Positions are never snapped to a
/// row or a column. The sweep order and the tie direction depend on node indexes only.
/// </summary>
internal static class GraphCardSeparator
{
    /// <summary>A penetration smaller than this is settled: it is a shortfall of the gap, never an overlap.</summary>
    private const double Tolerance = 0.5;

    /// <summary>Separates the cards in place until each pair keeps the gap, or the pass budget ends.</summary>
    /// <param name="nodes">The node index of each card.</param>
    /// <param name="centreX">Each card's centre x, parallel to the nodes; updated in place.</param>
    /// <param name="centreY">Each card's centre y, parallel to the nodes; updated in place.</param>
    /// <param name="widths">The card widths by node index.</param>
    /// <param name="gap">The empty space to keep between two cards.</param>
    /// <param name="passes">The most passes to make.</param>
    /// <returns>Whether the last pass found nothing to move.</returns>
    public static bool Separate(IReadOnlyList<int> nodes, double[] centreX, double[] centreY, IReadOnlyList<double> widths, double gap, int passes)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            if (!Pass(nodes, centreX, centreY, widths, gap))
            {
                return true;
            }
        }

        return !Pass(nodes, centreX, centreY, widths, gap);
    }

    /// <summary>
    /// The final settling of a seed: the pass budget first, then a last-resort legalization of whatever
    /// still overlaps, so a seed never carries two stacked cards into routing.
    /// </summary>
    /// <param name="nodes">The node index of each card.</param>
    /// <param name="centreX">Each card's centre x; updated in place.</param>
    /// <param name="centreY">Each card's centre y; updated in place.</param>
    /// <param name="widths">The card widths by node index.</param>
    public static void Settle(IReadOnlyList<int> nodes, double[] centreX, double[] centreY, IReadOnlyList<double> widths)
    {
        Separate(nodes, centreX, centreY, widths, GraphPlacementConstants.CardGap, GraphSearchBudget.MaximumSeparationPasses);
        Legalize(nodes, centreX, centreY, widths, GraphPlacementConstants.CardGap / 2);
    }

    /// <summary>
    /// Places the cards one at a time in left-edge order; a card that still overlaps an earlier one slides
    /// down to just below the card it hits and tries again. Only a card that is still in the way moves, and
    /// the walk always ends because a card only ever moves down.
    /// </summary>
    private static void Legalize(IReadOnlyList<int> nodes, double[] x, double[] y, IReadOnlyList<double> widths, double gap)
    {
        var cell = widths.Max() + gap;
        var placed = new Dictionary<(long, long), List<int>>();
        var order = Enumerable.Range(0, nodes.Count)
            .OrderBy(at => x[at] - (widths[nodes[at]] / 2)).ThenBy(at => nodes[at]).ToArray();

        foreach (var at in order)
        {
            while (Blocker(nodes, x, y, widths, gap, placed, cell, at) is { } hit)
            {
                y[at] = y[hit] + GraphCard.Height + gap + 1e-6;
            }

            var key = ((long)Math.Floor(x[at] / cell), (long)Math.Floor(y[at] / cell));
            if (!placed.TryGetValue(key, out var members))
            {
                placed[key] = members = [];
            }

            members.Add(at);
        }
    }

    private static int? Blocker(IReadOnlyList<int> nodes, double[] x, double[] y, IReadOnlyList<double> widths, double gap, Dictionary<(long, long), List<int>> placed, double cell, int at)
    {
        var (column, row) = ((long)Math.Floor(x[at] / cell), (long)Math.Floor(y[at] / cell));
        int? lowest = null;

        for (var cx = column - 1; cx <= column + 1; cx++)
        {
            for (var cy = row - 1; cy <= row + 1; cy++)
            {
                if (!placed.TryGetValue((cx, cy), out var members))
                {
                    continue;
                }

                foreach (var other in members)
                {
                    var hits = Math.Abs(x[at] - x[other]) < ((widths[nodes[at]] + widths[nodes[other]]) / 2) + gap - 1e-9
                        && Math.Abs(y[at] - y[other]) < GraphCard.Height + gap - 1e-9;
                    if (hits && (lowest is null || y[other] > y[lowest.Value]))
                    {
                        lowest = other;
                    }
                }
            }
        }

        return lowest;
    }
    private static bool Pass(IReadOnlyList<int> nodes, double[] x, double[] y, IReadOnlyList<double> widths, double gap)
    {
        var order = new int[nodes.Count];
        for (var at = 0; at < order.Length; at++)
        {
            order[at] = at;
        }

        // A total order (left edge, then node index), so the sweep never depends on the sort's stability.
        Array.Sort(order, (p, q) =>
        {
            var byLeft = (x[p] - (widths[nodes[p]] / 2)).CompareTo(x[q] - (widths[nodes[q]] / 2));
            return byLeft != 0 ? byLeft : nodes[p].CompareTo(nodes[q]);
        });        var moved = false;

        for (var first = 0; first < order.Length; first++)
        {
            var a = order[first];
            var rightA = x[a] + (widths[nodes[a]] / 2);

            for (var second = first + 1; second < order.Length; second++)
            {
                var b = order[second];
                if (x[b] - (widths[nodes[b]] / 2) >= rightA + gap)
                {
                    break;
                }

                moved |= Push(nodes, x, y, widths, gap, a, b);
            }
        }

        return moved;
    }

    private static bool Push(IReadOnlyList<int> nodes, double[] x, double[] y, IReadOnlyList<double> widths, double gap, int a, int b)
    {
        var (dx, dy) = (x[b] - x[a], y[b] - y[a]);
        var penetrationX = ((widths[nodes[a]] + widths[nodes[b]]) / 2) + gap - Math.Abs(dx);
        var penetrationY = GraphCard.Height + gap - Math.Abs(dy);

        if (penetrationX <= Tolerance || penetrationY <= Tolerance)
        {
            return false;
        }

        // The higher node index is the one pushed positive when two centres coincide, so a tie is not a coin toss.
        var sign = nodes[b] >= nodes[a] ? 1 : -1;
        if (penetrationX <= penetrationY)
        {
            var step = penetrationX / 2 * (dx == 0 ? sign : Math.Sign(dx));
            x[a] -= step;
            x[b] += step;
        }
        else
        {
            var step = penetrationY / 2 * (dy == 0 ? sign : Math.Sign(dy));
            y[a] -= step;
            y[b] += step;
        }

        return true;
    }
}
