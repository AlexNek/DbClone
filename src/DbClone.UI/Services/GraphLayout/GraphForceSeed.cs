namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// A deterministic force-directed seed for dense components: edge springs toward a preferred distance,
/// rectangle repulsion, a weak rank-direction force and a boundary force, over a fixed number of
/// iterations with a fixed cooling schedule and no random source. Overlaps are resolved after every
/// iteration along the minimum separating axis; nothing is snapped to a row or a column.
/// </summary>
internal static class GraphForceSeed
{
    private const double SpringStrength = 0.3;
    private const double RepelStrength = 0.4;
    private const double RankStrength = 0.05;
    private const double BoundaryStrength = 0.1;
    private const double GoldenAngle = 2.399963229728653;

    /// <summary>Places every component.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="variant">The parameter set.</param>
    /// <param name="cancellation">Checked between iterations.</param>
    /// <returns>The draft.</returns>
    public static GraphDraftLayout Place(GraphLayoutProfile profile, GraphForceVariant variant, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(variant);

        var locals = profile.ComponentNodes.Select(nodes => Component(profile, nodes, variant, cancellation)).ToArray();

        return GraphComponentPacker.Pack(variant.Name, profile, locals);
    }

    private static GraphLocalLayout Component(GraphLayoutProfile profile, IReadOnlyList<int> nodes, GraphForceVariant variant, CancellationToken cancellation)
    {
        var count = nodes.Count;
        var indexOf = nodes.Select((node, at) => (node, at)).ToDictionary(pair => pair.node, pair => pair.at);
        var edges = profile.Edges.Where(edge => edge.Child != edge.Parent && indexOf.ContainsKey(edge.Child))
            .Select(edge => (Child: indexOf[edge.Child], Parent: indexOf[edge.Parent])).ToArray();
        var (x, y) = Spiral(profile, nodes);
        var span = Math.Sqrt(nodes.Sum(node => (profile.Widths[node] + GraphPlacementConstants.CardGap) * (GraphCard.Height + GraphPlacementConstants.CardGap)) * GraphPlacementConstants.Aspect);
        var start = Math.Max(GraphCard.Height, span / 8);

        for (var iteration = 0; iteration < GraphSearchBudget.MaximumForceIterations && count > 1; iteration++)
        {
            cancellation.ThrowIfCancellationRequested();
            var temperature = start * (1 - ((double)iteration / GraphSearchBudget.MaximumForceIterations));
            var (dx, dy) = (new double[count], new double[count]);

            Attract(profile, nodes, edges, variant, x, y, dx, dy);
            Rank(profile, nodes, edges, variant, x, y, dx, dy);
            Repel(profile, nodes, x, y, dx, dy);

            for (var at = 0; at < count; at++)
            {
                var length = Math.Sqrt((dx[at] * dx[at]) + (dy[at] * dy[at]));
                var scale = length > temperature && length > 0 ? temperature / length : 1;
                x[at] += (dx[at] * scale) + Boundary(x[at], span);
                y[at] += (dy[at] * scale) + Boundary(y[at], span);
            }

            GraphCardSeparator.Separate(nodes, x, y, profile.Widths, GraphPlacementConstants.CardGap, 2);
        }

        GraphCardSeparator.Settle(nodes, x, y, profile.Widths);

        return GraphLocalLayout.FromCentres(nodes, x, y, profile.Widths);
    }

    /// <summary>A soft pull back toward the middle for a card beyond the span, so the component stays inside its canvas without a wall to pile against.</summary>
    private static double Boundary(double position, double span) =>
        Math.Abs(position) > span ? -BoundaryStrength * (position - (Math.Sign(position) * span)) : 0;

    /// <summary>A golden-angle spiral in node order: an even, random-free start.</summary>
    private static (double[] X, double[] Y) Spiral(GraphLayoutProfile profile, IReadOnlyList<int> nodes)
    {
        var cell = Math.Sqrt(nodes.Average(node => (profile.Widths[node] + GraphPlacementConstants.CardGap) * (GraphCard.Height + GraphPlacementConstants.CardGap)));
        var (x, y) = (new double[nodes.Count], new double[nodes.Count]);
        for (var at = 0; at < nodes.Count; at++)
        {
            var radius = cell * 0.9 * Math.Sqrt(at);
            x[at] = radius * Math.Cos(at * GoldenAngle);
            y[at] = radius * Math.Sin(at * GoldenAngle);
        }

        return (x, y);
    }

    private static void Attract(GraphLayoutProfile profile, IReadOnlyList<int> nodes, (int Child, int Parent)[] edges, GraphForceVariant variant, double[] x, double[] y, double[] dx, double[] dy)
    {
        foreach (var (child, parent) in edges)
        {
            var (vx, vy) = (x[parent] - x[child], y[parent] - y[child]);
            var distance = Math.Sqrt((vx * vx) + (vy * vy));
            if (distance < 1e-9)
            {
                continue;
            }

            var (ux, uy) = (vx / distance, vy / distance);
            var across = (profile.Widths[nodes[child]] + profile.Widths[nodes[parent]]) / 2;
            var preferred = (Math.Abs(ux) * (across + (GraphPlacementConstants.ColumnGap * variant.Spread)))
                + (Math.Abs(uy) * (GraphCard.Height + (GraphPlacementConstants.RowGap * variant.Spread)));
            var pull = SpringStrength * (distance - preferred) / 2;

            dx[child] += ux * pull;
            dy[child] += uy * pull;
            dx[parent] -= ux * pull;
            dy[parent] -= uy * pull;
        }
    }

    /// <summary>Pulls a child toward the rank direction below its parent, weakly.</summary>
    private static void Rank(GraphLayoutProfile profile, IReadOnlyList<int> nodes, (int Child, int Parent)[] edges, GraphForceVariant variant, double[] x, double[] y, double[] dx, double[] dy)
    {
        foreach (var (child, parent) in edges)
        {
            if (profile.Group[nodes[child]] == profile.Group[nodes[parent]])
            {
                continue;
            }

            if (variant.Transposed)
            {
                var wanted = x[parent] + ((profile.Widths[nodes[child]] + profile.Widths[nodes[parent]]) / 2) + GraphPlacementConstants.ColumnGap;
                dx[child] += RankStrength * (wanted - x[child]);
                dx[parent] -= RankStrength * (wanted - x[child]) / 2;
            }
            else
            {
                var wanted = y[parent] + GraphCard.Height + GraphPlacementConstants.RowGap;
                dy[child] += RankStrength * (wanted - y[child]);
                dy[parent] -= RankStrength * (wanted - y[child]) / 2;
            }
        }
    }

    /// <summary>Pushes overlapping cards apart; only cards in the same or an adjacent grid cell can interact.</summary>
    private static void Repel(GraphLayoutProfile profile, IReadOnlyList<int> nodes, double[] x, double[] y, double[] dx, double[] dy)
    {
        var cell = nodes.Max(node => profile.Widths[node]) + GraphPlacementConstants.CardGap;
        var buckets = new Dictionary<(long, long), List<int>>();
        for (var at = 0; at < nodes.Count; at++)
        {
            var key = ((long)Math.Floor(x[at] / cell), (long)Math.Floor(y[at] / cell));
            if (!buckets.TryGetValue(key, out var members))
            {
                buckets[key] = members = [];
            }

            members.Add(at);
        }

        // Cells are visited in a fixed order, so the sums that reach each card do not depend on hashing.
        foreach (var ((cx, cy), members) in buckets.OrderBy(bucket => bucket.Key.Item1).ThenBy(bucket => bucket.Key.Item2))
        {
            foreach (var (ox, oy) in new[] { (0L, 0L), (1L, 0L), (-1L, 1L), (0L, 1L), (1L, 1L) })
            {
                if (!buckets.TryGetValue((cx + ox, cy + oy), out var others))
                {
                    continue;
                }

                foreach (var a in members)
                {
                    foreach (var b in others)
                    {
                        if ((ox, oy) != (0L, 0L) || b > a)
                        {
                            PushApart(profile, nodes, x, y, dx, dy, a, b);
                        }
                    }
                }
            }
        }
    }
    private static void PushApart(GraphLayoutProfile profile, IReadOnlyList<int> nodes, double[] x, double[] y, double[] dx, double[] dy, int a, int b)
    {
        var (vx, vy) = (x[b] - x[a], y[b] - y[a]);
        var penetrationX = ((profile.Widths[nodes[a]] + profile.Widths[nodes[b]]) / 2) + GraphPlacementConstants.CardGap - Math.Abs(vx);
        var penetrationY = GraphCard.Height + GraphPlacementConstants.CardGap - Math.Abs(vy);
        if (penetrationX <= 0 || penetrationY <= 0)
        {
            return;
        }

        var sign = nodes[b] >= nodes[a] ? 1 : -1;
        if (penetrationX <= penetrationY)
        {
            var push = RepelStrength * penetrationX / 2 * (vx == 0 ? sign : Math.Sign(vx));
            dx[a] -= push;
            dx[b] += push;
        }
        else
        {
            var push = RepelStrength * penetrationY / 2 * (vy == 0 ? sign : Math.Sign(vy));
            dy[a] -= push;
            dy[b] += push;
        }
    }
}
