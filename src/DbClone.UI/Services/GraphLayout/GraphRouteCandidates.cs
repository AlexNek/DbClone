namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The deterministic candidate families for one relation: the direct segment, then smooth cubic bows, never
/// a horizontal and vertical corridor. A bow's controls leave each port along its outward normal, so the
/// tangent never runs along the card border.
/// </summary>
internal static class GraphRouteCandidates
{
    /// <summary>The first bow offset and the increment of the symmetric sequence: <c>max(centerline distance, clearance + stroke)</c>.</summary>
    public static readonly double OffsetStep = Math.Max(
        GraphGeometryContract.MinimumCenterlineDistance,
        GraphGeometryContract.CardClearance + GraphGeometryContract.StrokeWidth);

    private const int IncrementalPairs = 8;
    private const int NearPairs = 3;

    /// <summary>The threaded-curve policies, in order: nearer side per card, then all to one side, then the other, each in a near and a far lane.</summary>
    private static readonly (int Side, double Lane)[] ThreadPolicies =
    [
        (0, 18), (1, 18), (-1, 18), (0, 36), (1, 36), (-1, 36), (0, 8), (1, 8), (-1, 8),
    ];
    private static readonly double[] FallbackOffsets = [80, 120, 180, 260, 380, 540];

    /// <summary>Enumerates the candidates of an ordinary relation.</summary>
    /// <param name="start">The child's port.</param>
    /// <param name="end">The parent's port.</param>
    /// <param name="blockers">The cards the direct segment would cross, by node index.</param>
    /// <param name="cards">All cards, by node index.</param>
    /// <param name="parallelOffset">The signed offset reserved for this relation within its parallel family; zero when it has none.</param>
    /// <param name="own">The two cards the relation joins.</param>
    /// <returns>At most <see cref="GraphSearchBudget.MaximumRouteCandidatesPerEdge"/> candidates, in a fixed order.</returns>
    public static IReadOnlyList<GraphRouteCandidate> For(
        GraphEdgePort start,
        GraphEdgePort end,
        IReadOnlyList<int> blockers,
        IReadOnlyList<NodePlacement> cards,
        double parallelOffset,
        (int Child, int Parent) own)
    {
        var list = new List<GraphRouteCandidate> { new(0, EEdgeGeometry.Straight, [start.Point, end.Point]) };
        var tried = new List<double>();

        void Bow(double offset)
        {
            if (list.Count < GraphSearchBudget.MaximumRouteCandidatesPerEdge && !tried.Any(known => Math.Abs(known - offset) < 1))
            {
                tried.Add(offset);
                list.Add(new GraphRouteCandidate(list.Count, EEdgeGeometry.Cubic, BowPoints(start, end, offset)));
            }
        }

        if (parallelOffset != 0)
        {
            Bow(parallelOffset);
        }

        for (var pair = 1; pair <= NearPairs; pair++)
        {
            Bow(-pair * OffsetStep);
            Bow(pair * OffsetStep);
        }

        foreach (var offset in Around(start.Point, end.Point, blockers, cards))
        {
            Bow(offset);
        }

        if (blockers.Count > 0)
        {
            var seen = new List<GraphPoint[]>();
            foreach (var (side, lane) in ThreadPolicies)
            {
                if (list.Count >= GraphSearchBudget.MaximumRouteCandidatesPerEdge)
                {
                    break;
                }

                var points = GraphThreadedCurve.Build(start, end, blockers, cards, own, side, lane);
                if (points is not null && !seen.Any(known => known.SequenceEqual(points)))
                {
                    seen.Add(points);
                    list.Add(new GraphRouteCandidate(list.Count, EEdgeGeometry.Cubic, points));
                }
            }
        }

        for (var pair = NearPairs + 1; pair <= IncrementalPairs; pair++)
        {
            Bow(-pair * OffsetStep);
            Bow(pair * OffsetStep);
        }

        foreach (var fallback in FallbackOffsets)
        {
            Bow(-fallback);
            Bow(fallback);
        }

        return list;
    }
    /// <summary>Enumerates the loop candidates of a self-reference: growing arcs from its child port to its parent port.</summary>
    /// <param name="start">The child's port.</param>
    /// <param name="end">The parent's port.</param>
    /// <returns>The candidates, smallest first.</returns>
    public static IReadOnlyList<GraphRouteCandidate> ForLoop(GraphEdgePort start, GraphEdgePort end)
    {
        var list = new List<GraphRouteCandidate>();
        for (var step = 0; step < 12; step++)
        {
            var reach = 36 + (step * GraphGeometryContract.MinimumPortStep * 2);
            list.Add(new GraphRouteCandidate(
                step,
                EEdgeGeometry.Cubic,
                [
                    start.Point,
                    new GraphPoint(start.Point.X + (start.OutwardX * reach), start.Point.Y + (start.OutwardY * reach)),
                    new GraphPoint(end.Point.X + (end.OutwardX * reach), end.Point.Y + (end.OutwardY * reach)),
                    end.Point,
                ]));
        }

        return list;
    }

    /// <summary>
    /// A cubic whose apex stands <paramref name="offset"/> from the chord. The controls leave the ports along
    /// their outward normals and carry the sideways share of the bow.
    /// </summary>
    private static GraphPoint[] BowPoints(GraphEdgePort start, GraphEdgePort end, double offset)
    {
        var (dx, dy) = (end.Point.X - start.Point.X, end.Point.Y - start.Point.Y);
        var length = Math.Max(1e-9, Math.Sqrt((dx * dx) + (dy * dy)));
        var (perpX, perpY) = (-dy / length, dx / length);
        var sideways = offset / 0.75;
        var reach = Math.Max(Math.Max(length / 3, Math.Abs(sideways) * 0.8), 24);

        return
        [
            start.Point,
            new GraphPoint(
                start.Point.X + (start.OutwardX * reach) + (perpX * sideways),
                start.Point.Y + (start.OutwardY * reach) + (perpY * sideways)),
            new GraphPoint(
                end.Point.X + (end.OutwardX * reach) + (perpX * sideways),
                end.Point.Y + (end.OutwardY * reach) + (perpY * sideways)),
            end.Point,
        ];
    }

    /// <summary>The offsets that clear each blocking card from either side, then the one that clears them all.</summary>
    private static IEnumerable<double> Around(GraphPoint start, GraphPoint end, IReadOnlyList<int> blockers, IReadOnlyList<NodePlacement> cards)
    {
        var (dx, dy) = (end.X - start.X, end.Y - start.Y);
        var length = Math.Max(1e-9, Math.Sqrt((dx * dx) + (dy * dy)));
        var (perpX, perpY) = (-dy / length, dx / length);
        double positive = 0, negative = 0;
        var offsets = new List<double>();

        foreach (var index in blockers.OrderBy(index => index))
        {
            var card = cards[index];
            var grow = GraphGeometryContract.CardCenterlineClearance;
            var corners = new[]
            {
                new GraphPoint(card.TopLeft.X - grow, card.TopLeft.Y - grow),
                new GraphPoint(card.Right + grow, card.TopLeft.Y - grow),
                new GraphPoint(card.TopLeft.X - grow, card.Bottom + grow),
                new GraphPoint(card.Right + grow, card.Bottom + grow),
            };
            var signed = corners.Select(corner => ((corner.X - start.X) * perpX) + ((corner.Y - start.Y) * perpY)).ToArray();
            var along = (((card.Centre.X - start.X) * dx) + ((card.Centre.Y - start.Y) * dy)) / (length * length);
            var share = Math.Max(0.35, 4 * along * (1 - along));

            var up = Math.Max(0, signed.Max()) / share;
            var down = Math.Max(0, -signed.Min()) / share;
            positive = Math.Max(positive, up);
            negative = Math.Max(negative, down);
            offsets.Add(-(down + GraphGeometryContract.MinimumCenterlineDistance));
            offsets.Add(up + GraphGeometryContract.MinimumCenterlineDistance);
        }

        if (blockers.Count > 1)
        {
            offsets.Add(-(negative + GraphGeometryContract.MinimumCenterlineDistance));
            offsets.Add(positive + GraphGeometryContract.MinimumCenterlineDistance);
        }

        return offsets;
    }
}
