namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The ranked seed: each component's rank order with barycentre sweeps to cut crossings, laid out as a
/// tidy forest. Every table stands under the parent it points at, and a rank wider than the page is dealt
/// into bands no wider than it, so a table a dozen others name is read as a fan of rows under it instead
/// of one row several screens long. A table that rules a fan is widened to the span its band of children
/// covers, so those children meet its border and the fan leaves on the one side its relations ask for. A
/// rank that fits the page is drawn as the one line the forest measured for it. A table that names several
/// parents stands under the nearest-ranked one and its other relations are routed as curves.
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

        var (x, widths) = Forest(profile, rows);

        // The page is measured on the cards' own text, never on a table stretched across its fan: a bar as
        // wide as the row it rules would make every row it stands in "fit" and none of them ever fold.
        var natural = profile.Widths.ToArray();
        var page = PageWidth(rows, natural);
        var bands = Bands(rows, natural, x, page);

        // A rank that fits the page is dealt as the one line the tidy forest already gave it, at the columns
        // that forest measured: only a rank wider than the page is folded, so a catalog that always fitted
        // is arranged exactly as it was. A folded rank is a rank of its own, packed band by band and centred
        // under the tables its own tables name.
        if (bands.Count == rows.Count)
        {
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

        var ladder = Widen(profile, bands, natural, page);

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
            var total = members.Sum(node => ladder[node])
                + ((members.Count - 1) * GraphPlacementConstants.ColumnGap);
            var left = (Anchor(profile, members, placed) ?? carried) - (total / 2);
            var cursor = left;

            foreach (var node in members)
            {
                order.Add(node);
                centreX.Add(cursor + (ladder[node] / 2));
                centreY.Add((band * (GraphCard.Height + GraphPlacementConstants.RowGap)) + (GraphCard.Height / 2));
                placed[node] = cursor + (ladder[node] / 2);
                cursor += ladder[node] + GraphPlacementConstants.ColumnGap;
            }

            carried = left + (total / 2);
        }

        return (GraphLocalLayout.FromCentres(order, centreX, centreY, ladder), ladder);
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
    /// narrower than one card and its gap. A catalog of fifteen cards reaches a page of two screens of
    /// narrow cards, which is the width a reader can follow without leaving the fan.
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
    /// Widens a table that rules a fan to the border its ends need: at least the width one border holds
    /// them all on, and at most the span its children cover inside one band, so every end of the fan faces
    /// the border the children stand under and no arrow leaves another side to turn round the table.
    /// Bounded by the page: a fan folded into bands is a fan of bands, not of screens.
    /// </summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="bands">The component's bands, shallowest rank first.</param>
    /// <param name="widths">The card widths, by node index.</param>
    /// <param name="page">The widest band.</param>
    /// <returns>The width every card draws at.</returns>
    private static double[] Widen(
        GraphLayoutProfile profile,
        IReadOnlyList<List<int>> bands,
        IReadOnlyList<double> widths,
        double page)
    {
        var ladder = widths.ToArray();
        var children = Children(profile);
        var fan = FanSizes(profile);

        foreach (var band in bands)
        {
            var left = new Dictionary<int, double>();
            var cursor = 0.0;
            foreach (var node in band)
            {
                left[node] = cursor;
                cursor += widths[node] + GraphPlacementConstants.ColumnGap;
            }

            foreach (var node in band)
            {
                var below = (children.TryGetValue(node, out var list) ? list : []).Where(left.ContainsKey).ToArray();
                var count = Math.Max(below.Length, fan.GetValueOrDefault(node));
                if (count < 2)
                {
                    continue;
                }

                // The ends of a fan all meet one border, so a card a dozen tables name is at least wide enough
                // to deal them: a narrow card leaves the last of them no port at all, whatever the bands do.
                var span = below.Length == 0
                    ? 0
                    : below.Max(child => left[child] + widths[child]) - below.Min(child => left[child]);
                ladder[node] = Math.Max(
                    ladder[node],
                    Math.Min(page, Math.Max(span, GraphPortAssigner.BorderWidth(count))));
            }
        }

        return ladder;
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

    /// <summary>The tables that name each table.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <returns>The children of every table, by parent.</returns>
    private static Dictionary<int, List<int>> Children(GraphLayoutProfile profile)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var (child, parent) in profile.Edges)
        {
            if (child == parent)
            {
                continue;
            }

            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(child);
        }

        return children;
    }

    /// <summary>
    /// Gives every table the column interval of its subtree. Children stand in their row order under the
    /// parent; the parent is centred over them. Ranks only ever increase from parent to child, so positions
    /// are settled from the shallowest rank down and widths from the deepest rank up, with no recursion.
    /// </summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="rows">The component's nodes by rank.</param>
    /// <returns>Each table's centre x and the width it draws at, both by node index.</returns>
    private static (Dictionary<int, double> X, double[] Widths) Forest(GraphLayoutProfile profile, List<List<int>> rows)
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
            var own = profile.Widths[node] + GraphPlacementConstants.ColumnGap;
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

        return (x, Widths(profile, all, children, x));
    }

    /// <summary>
    /// Widens a table that stands over a fan to the span its children cover, so every end of the fan meets
    /// the border it stands under and no arrow leaves another side to turn round the table. Deepest rank
    /// first, so a widened card is measured by its own final width when its parent is widened. A table that
    /// stands over one table at most keeps its own width: two cards of one relation already face each other.
    /// </summary>
    private static double[] Widths(
        GraphLayoutProfile profile,
        IReadOnlyList<int> all,
        Dictionary<int, List<int>> children,
        Dictionary<int, double> x)
    {
        var widths = profile.Widths.ToArray();
        var fan = FanSizes(profile);
        foreach (var node in all.OrderByDescending(node => profile.Rank[node]).ThenBy(node => node))
        {
            var below = children.TryGetValue(node, out var under) ? under : [];
            var count = Math.Max(below.Count, fan.GetValueOrDefault(node));
            if (count < 2)
            {
                continue;
            }

            var left = below.Count == 0 ? x[node] : below.Min(child => x[child] - (widths[child] / 2));
            var right = below.Count == 0 ? x[node] : below.Max(child => x[child] + (widths[child] / 2));

            widths[node] = Math.Max(widths[node], right - left);
        }

        return widths;
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
    /// left-packed and the right-packed solutions, both of which keep the separation.
    /// </summary>
    private static void Refine(GraphLayoutProfile profile, List<List<int>> rows, Dictionary<int, double> x)
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

                double Separation(int at) => ((profile.Widths[row[at]] + profile.Widths[row[at + 1]]) / 2) + GraphPlacementConstants.ColumnGap;

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
