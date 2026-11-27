namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The ranked seed: each component's rank order with barycentre sweeps to cut crossings, laid out as a
/// tidy forest. Every table stands under the parent it points at, and a rank wider than the page is dealt
/// into bands no wider than it, so a table a dozen others name is read as a fan of rows under it instead
/// of one row several screens long. A table that rules a fan is widened until one border can deal every
/// end that meets it. A rank that fits the page is drawn as the one line the forest measured for it. A
/// table that names several parents stands under the nearest-ranked one and its other relations are drawn
/// as chords.
/// </summary>
internal static class GraphRankedSeed
{
    private const int Sweeps = 4;

    /// <summary>How many damped barycentre passes follow the tidy forest.</summary>
    private const int RefinePasses = 6;

    /// <summary>The narrowest page a rank is folded to, so a page of two wide cards is still a page.</summary>
    private const double MinimumBandWidth = 720;

    /// <summary>Places every component.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <returns>The draft.</returns>
    public static GraphDraftLayout Place(GraphLayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var widths = profile.Widths.ToArray();
        var locals = new List<GraphLocalLayout>(profile.ComponentNodes.Count);

        foreach (var nodes in profile.ComponentNodes)
        {
            var (local, widened) = Component(profile, nodes);
            foreach (var node in nodes)
            {
                widths[node] = widened[node];
            }

            locals.Add(local);
        }

        return GraphComponentPacker.Pack("ranked", widths, locals);
    }

    private static (GraphLocalLayout Local, double[] Widths) Component(GraphLayoutProfile profile, IReadOnlyList<int> nodes)
    {
        var rows = nodes.GroupBy(node => profile.Rank[node]).OrderBy(group => group.Key)
            .Select(group => group.OrderBy(node => node).ToList()).ToList();

        Sweep(profile, rows);

        // A card draws at its own text, or wider when its fan needs the border; nothing is stretched across
        // its children, so a band folded on these widths stays the page it was folded to.
        var widths = FanWidths(profile);
        var x = Forest(profile, rows, widths);

        var page = PageWidth(rows, widths);
        var bands = Bands(rows, widths, x, page);

        // A rank that fits the page is dealt as the one line the tidy forest measured for it: only a rank
        // wider than the page is folded, so a catalog that always fitted is arranged exactly as it was.
        if (bands.Count == rows.Count)
        {
            Refine(profile, rows, x, widths);

            var line = new List<int>(nodes.Count);
            var at = new List<double>(nodes.Count);
            var down = new List<double>(nodes.Count);
            for (var row = 0; row < rows.Count; row++)
            {
                foreach (var node in rows[row].OrderBy(node => x[node]).ThenBy(node => node))
                {
                    line.Add(node);
                    at.Add(x[node]);
                    down.Add((row * (GraphCard.Height + GraphPlacementConstants.RowGap)) + (GraphCard.Height / 2));
                }
            }

            return (GraphLocalLayout.FromCentres(line, at, down, widths), widths);
        }

        var order = new List<int>(nodes.Count);
        var centreX = new List<double>(nodes.Count);
        var centreY = new List<double>(nodes.Count);
        var placed = new Dictionary<int, double>();
        var carried = 0.0;

        for (var band = 0; band < bands.Count; band++)
        {
            var members = bands[band];

            // A band stands under the tables its own tables name, and the first band of a component has
            // nobody above it: it is carried on from the band before, so a component never leaves a gap.
            var total = members.Sum(node => widths[node])
                + ((members.Count - 1) * GraphPlacementConstants.ColumnGap);
            var left = (Anchor(profile, members, placed) ?? carried) - (total / 2);
            var cursor = left;

            foreach (var node in members)
            {
                order.Add(node);
                centreX.Add(cursor + (widths[node] / 2));
                centreY.Add((band * (GraphCard.Height + GraphPlacementConstants.RowGap)) + (GraphCard.Height / 2));
                placed[node] = cursor + (widths[node] / 2);
                cursor += widths[node] + GraphPlacementConstants.ColumnGap;
            }

            carried = left + (total / 2);
        }

        return (GraphLocalLayout.FromCentres(order, centreX, centreY, widths), widths);
    }

    /// <summary>
    /// Divides every rank into bands no wider than the page, in the order the sweeps settled. A rank of
    /// thirteen tables was one row several screens long, which reads as a band of text rather than as a fan;
    /// folded, it is the two or three rows a reader can see at once. Bands keep the rank order, so a table
    /// still stands below every table it names.
    /// </summary>
    /// <param name="rows">The component's nodes by rank, in the settled order.</param>
    /// <param name="widths">The card widths, by node index.</param>
    /// <param name="x">The tidy forest's column position, which is the order of a row.</param>
    /// <param name="page">The widest band.</param>
    /// <returns>The bands, shallowest rank first.</returns>
    private static List<List<int>> Bands(
        IReadOnlyList<List<int>> rows,
        IReadOnlyList<double> widths,
        IReadOnlyDictionary<int, double> x,
        double page)
    {
        var bands = new List<List<int>>();

        foreach (var row in rows)
        {
            var band = new List<int>();
            var used = 0.0;

            foreach (var node in row.OrderBy(node => x[node]).ThenBy(node => node))
            {
                var need = band.Count == 0 ? widths[node] : GraphPlacementConstants.ColumnGap + widths[node];
                if (band.Count > 0 && used + need > page)
                {
                    bands.Add(band);
                    band = [];
                    used = 0;
                    need = widths[node];
                }

                band.Add(node);
                used += need;
            }

            if (band.Count > 0)
            {
                bands.Add(band);
            }
        }

        return bands;
    }

    /// <summary>
    /// The widest band a component's tables are folded into: the page the cards' own area asks for, never
    /// narrower than one card and its gap.
    /// </summary>
    /// <param name="rows">The component's nodes by rank.</param>
    /// <param name="widths">The card widths, by node index.</param>
    /// <returns>The page width.</returns>
    private static double PageWidth(IReadOnlyList<List<int>> rows, IReadOnlyList<double> widths)
    {
        var cells = rows.SelectMany(row => row)
            .Sum(node => (widths[node] + GraphPlacementConstants.ColumnGap) * (GraphCard.Height + GraphPlacementConstants.RowGap));
        var widest = rows.SelectMany(row => row).Max(node => widths[node]);

        return Math.Max(
            widest + GraphPlacementConstants.ColumnGap,
            Math.Max(MinimumBandWidth, Math.Sqrt(cells * GraphPlacementConstants.Aspect)));
    }

    /// <summary>
    /// The width every card draws at: its own text, or the border one side needs to deal the ends that meet
    /// it — a card a dozen tables name is at least wide enough to give each of them a port.
    /// </summary>
    /// <param name="profile">The graph facts.</param>
    /// <returns>The width every card draws at.</returns>
    private static double[] FanWidths(GraphLayoutProfile profile)
    {
        var widths = profile.Widths.ToArray();
        var fan = FanSizes(profile);
        for (var node = 0; node < widths.Length; node++)
        {
            var count = fan.GetValueOrDefault(node);
            if (count > 1)
            {
                widths[node] = Math.Max(widths[node], GraphPortAssigner.BorderWidth(count));
            }
        }

        return widths;
    }

    /// <summary>Where a band wants to stand: the mean of the tables its own tables name, once those are placed.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="members">The band's tables.</param>
    /// <param name="placed">The centre x of every table already placed.</param>
    /// <returns>The wanted centre x, or null when the band names nothing above it.</returns>
    private static double? Anchor(GraphLayoutProfile profile, IReadOnlyList<int> members, IReadOnlyDictionary<int, double> placed)
    {
        var wanted = new List<double>();
        foreach (var node in members)
        {
            var above = profile.Neighbours[node].Where(placed.ContainsKey).Select(next => placed[next]).ToArray();
            if (above.Length > 0)
            {
                wanted.Add(above.Average());
            }
        }

        return wanted.Count == 0 ? null : wanted.Average();
    }

    /// <summary>
    /// Gives every table the column interval of its subtree. Children stand in their row order under the
    /// parent; the parent is centred over them. Ranks only ever increase from parent to child, so positions
    /// are settled from the shallowest rank down with no recursion.
    /// </summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="rows">The component's nodes by rank.</param>
    /// <param name="widths">The width every card draws at, by node index.</param>
    /// <returns>Each table's centre x, by node index.</returns>
    private static Dictionary<int, double> Forest(GraphLayoutProfile profile, List<List<int>> rows, IReadOnlyList<double> widths)
    {
        var position = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            for (var at = 0; at < row.Count; at++)
            {
                position[row[at]] = at;
            }
        }

        var treeParent = new Dictionary<int, int>();
        var children = new Dictionary<int, List<int>>();
        var fan = FanSizes(profile);
        foreach (var node in rows.SelectMany(row => row))
        {
            var above = profile.Neighbours[node].Where(next => profile.Rank[next] < profile.Rank[node]).ToArray();
            if (above.Length == 0)
            {
                continue;
            }

            var parent = above.OrderByDescending(next => profile.Rank[next]).ThenByDescending(next => fan.GetValueOrDefault(next))
                .ThenBy(next => position[next]).ThenBy(next => next).First();
            treeParent[node] = parent;
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(node);
        }

        foreach (var list in children.Values)
        {
            list.Sort((a, b) => position[a] != position[b] ? position[a].CompareTo(position[b]) : a.CompareTo(b));
        }

        var all = rows.SelectMany(row => row).ToList();
        var span = new Dictionary<int, double>();
        foreach (var node in all.OrderByDescending(node => profile.Rank[node]).ThenBy(node => node))
        {
            var own = widths[node] + GraphPlacementConstants.ColumnGap;
            span[node] = children.TryGetValue(node, out var below) ? Math.Max(own, below.Sum(child => span[child])) : own;
        }

        var x = new Dictionary<int, double>();
        var left = new Dictionary<int, double>();
        var cursor = 0.0;
        foreach (var root in all.Where(node => !treeParent.ContainsKey(node)).OrderBy(node => profile.Rank[node]).ThenBy(node => position[node]))
        {
            left[root] = cursor;
            cursor += span[root];
        }

        foreach (var node in all.OrderBy(node => profile.Rank[node]).ThenBy(node => node))
        {
            x[node] = left[node] + (span[node] / 2);
            if (!children.TryGetValue(node, out var below))
            {
                continue;
            }

            var start = left[node] + ((span[node] - below.Sum(child => span[child])) / 2);
            foreach (var child in below)
            {
                left[child] = start;
                start += span[child];
            }
        }

        return x;
    }

    /// <summary>
    /// How many tables name each table. Two parents of one table at the same rank are settled by this, so a
    /// table that names both the hub of a fan and a table beside it stands in the fan.
    /// </summary>
    private static Dictionary<int, int> FanSizes(GraphLayoutProfile profile)
    {
        var counts = new Dictionary<int, int>();
        foreach (var (_, parent) in profile.Edges)
        {
            counts[parent] = counts.GetValueOrDefault(parent) + 1;
        }

        return counts;
    }

    /// <summary>
    /// Pulls a table that names several parents toward all of them. Each pass moves every row halfway to the
    /// mean of its neighbours above and below, and keeps the row's order and the column gap by averaging the
    /// left-packed and the right-packed solutions, both of which keep the separation. The separation is
    /// measured on the widths the cards draw at, so a table widened across its fan never buries a neighbour.
    /// </summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="rows">The component's nodes by rank, in the settled order.</param>
    /// <param name="x">Each table's centre x, by node index; refined in place.</param>
    /// <param name="widths">The width every card draws at, by node index.</param>
    private static void Refine(GraphLayoutProfile profile, List<List<int>> rows, Dictionary<int, double> x, IReadOnlyList<double> widths)
    {
        for (var pass = 0; pass < RefinePasses; pass++)
        {
            foreach (var row in rows)
            {
                var desired = row.Select(node =>
                {
                    var near = profile.Neighbours[node].Where(next => x.ContainsKey(next) && profile.Rank[next] != profile.Rank[node]).Select(next => x[next]).ToArray();

                    return near.Length == 0 ? x[node] : (x[node] + near.Average()) / 2;
                }).ToArray();

                double Separation(int at) => ((widths[row[at]] + widths[row[at + 1]]) / 2) + GraphPlacementConstants.ColumnGap;

                var left = (double[])desired.Clone();
                for (var at = 1; at < left.Length; at++)
                {
                    left[at] = Math.Max(left[at], left[at - 1] + Separation(at - 1));
                }

                var right = (double[])desired.Clone();
                for (var at = right.Length - 2; at >= 0; at--)
                {
                    right[at] = Math.Min(right[at], right[at + 1] - Separation(at));
                }

                for (var at = 0; at < row.Count; at++)
                {
                    x[row[at]] = (left[at] + right[at]) / 2;
                }
            }
        }
    }

    /// <summary>Orders each row by the mean position of its neighbours in the row above, then below.</summary>
    private static void Sweep(GraphLayoutProfile profile, List<List<int>> rows)
    {
        for (var pass = 0; pass < Sweeps; pass++)
        {
            for (var row = 1; row < rows.Count; row++)
            {
                Order(profile, rows[row], rows[row - 1]);
            }

            for (var row = rows.Count - 2; row >= 0; row--)
            {
                Order(profile, rows[row], rows[row + 1]);
            }
        }
    }

    private static void Order(GraphLayoutProfile profile, List<int> row, List<int> reference)
    {
        var position = new Dictionary<int, double>();
        for (var at = 0; at < reference.Count; at++)
        {
            position[reference[at]] = reference.Count == 1 ? 0.5 : (double)at / (reference.Count - 1);
        }

        var keys = new Dictionary<int, double>();
        for (var at = 0; at < row.Count; at++)
        {
            var mine = row.Count == 1 ? 0.5 : (double)at / (row.Count - 1);
            var near = profile.Neighbours[row[at]].Where(position.ContainsKey).Select(neighbour => position[neighbour]).ToArray();
            keys[row[at]] = near.Length == 0 ? mine : near.Average();
        }

        var previous = row.Select((node, at) => (node, at)).ToDictionary(pair => pair.node, pair => pair.at);
        var sorted = row.OrderBy(node => keys[node]).ThenBy(node => previous[node]).ThenBy(node => node).ToList();
        row.Clear();
        row.AddRange(sorted);
    }
}
