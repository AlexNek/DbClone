namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The soft ordering of two valid layouts, compared field by field in the order declared and never as a
/// weighted sum, so no count can buy a relation drawn up the page and a short route can never buy a
/// crossing. The hard validity every layout must have first is not a field: a layout with any hard failure
/// is not scored at all.
/// </summary>
/// <param name="Crossings">The admissible crossings; zero is the target.</param>
/// <param name="RankViolations">Relations whose child card's centre stands above its parent's.</param>
/// <param name="BlockedRelations">
/// Relations whose own straight stroke is drawn over a card it does not join. It is scored after rank, so a
/// clearer picture never buys a relation drawn up the page, and before the cluster terms, because a relation
/// running through an unrelated table is the defect a reader notices first: they cannot tell which table the
/// line belongs to. A layout with every relation in clear ground scores zero.
/// </param>
/// <param name="ClusterOverlap">
/// The summed area two related communities' bounding regions share. It is scored only after crossings and
/// rank, so a better-grouped picture never buys a relation drawn up the page or a crossing; among layouts
/// that stand top-down it is what prefers the one whose communities sit apart over the one that interleaves
/// them. A layout of a graph that splits into no community scores zero.
/// </param>
/// <param name="TotalLength">The summed arclength of every route.</param>
/// <param name="MaximumLength">The longest single route.</param>
/// <param name="TotalDeviation">The summed greatest distance of each route from its direct segment.</param>
/// <param name="ComponentArea">The summed bounding-box area of the connected components.</param>
/// <param name="CanvasArea">The canvas area.</param>
/// <param name="MaximumCardDistance">The greatest distance between the centres of two cards a relation joins.</param>
/// <param name="SeedOrdinal">The seed's position, which settles every remaining tie.</param>
internal readonly record struct GraphLayoutScore(
    int Crossings,
    int RankViolations,
    int BlockedRelations,
    double ClusterOverlap,
    double TotalLength,
    double MaximumLength,
    double TotalDeviation,
    double ComponentArea,
    double CanvasArea,
    double MaximumCardDistance,
    int SeedOrdinal) : IComparable<GraphLayoutScore>
{
    private const double Tolerance = 1e-6;

    /// <summary>Scores a valid layout.</summary>
    /// <param name="candidate">The complete valid layout.</param>
    /// <param name="profile">The graph facts.</param>
    /// <param name="seedOrdinal">The seed's position in the seed order.</param>
    /// <returns>The score.</returns>
    public static GraphLayoutScore Of(GraphLayoutCandidate candidate, GraphLayoutProfile profile, int seedOrdinal)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);

        var nodes = candidate.Layout.Nodes;
        var lengths = candidate.Routes.Select(route => Flat(route)).Select(points => GraphGeometry.ArcLength(points)).ToArray();
        var deviation = candidate.Routes.Select(route => Deviation(Flat(route))).Sum();
        var violations = MeasureRankViolations(profile, nodes);
        var distance = profile.Edges.Count == 0
            ? 0
            : profile.Edges.Max(edge => GraphGeometry.Distance(nodes[edge.Child].Centre, nodes[edge.Parent].Centre));
        var overlap = MeasureClusterOverlap(profile, nodes);
        var blocked = MeasureBlockedRelations(profile, nodes);

        return new GraphLayoutScore(
            candidate.CrossingCount,
            violations,
            blocked,
            overlap,
            lengths.Sum(),
            lengths.Length == 0 ? 0 : lengths.Max(),
            deviation,
            profile.ComponentNodes.Sum(members => Area(members.Select(member => nodes[member]).ToArray())),
            candidate.Layout.Width * candidate.Layout.Height,
            distance,
            seedOrdinal);
    }

    /// <inheritdoc/>
    public int CompareTo(GraphLayoutScore other)
    {
        var byCrossings = Crossings.CompareTo(other.Crossings);
        if (byCrossings != 0)
        {
            return byCrossings;
        }

        var byRank = RankViolations.CompareTo(other.RankViolations);
        if (byRank != 0)
        {
            return byRank;
        }

        var byBlocked = BlockedRelations.CompareTo(other.BlockedRelations);
        if (byBlocked != 0)
        {
            return byBlocked;
        }

        if (Math.Abs(ClusterOverlap - other.ClusterOverlap) > Tolerance)
        {
            return ClusterOverlap.CompareTo(other.ClusterOverlap);
        }

        foreach (var (mine, theirs) in new[]
        {
            (TotalLength, other.TotalLength),
            (MaximumLength, other.MaximumLength),
            (TotalDeviation, other.TotalDeviation),
            (ComponentArea, other.ComponentArea),
            (CanvasArea, other.CanvasArea),
            (MaximumCardDistance, other.MaximumCardDistance),
        })
        {
            if (Math.Abs(mine - theirs) > Tolerance)
            {
                return mine.CompareTo(theirs);
            }
        }

        return SeedOrdinal.CompareTo(other.SeedOrdinal);
    }

    /// <summary>Counts upward relations, excluding self-references and relations inside a cyclic group.</summary>
    internal static int MeasureRankViolations(GraphLayoutProfile profile, IReadOnlyList<NodePlacement> nodes) =>
        profile.Edges.Count(edge =>
            edge.Child != edge.Parent
            && !(profile.Group[edge.Child] == profile.Group[edge.Parent] && profile.GroupSize[edge.Child] > 1)
            && nodes[edge.Child].Centre.Y < nodes[edge.Parent].Centre.Y - Tolerance);

    private static IReadOnlyList<GraphPoint> Flat(EdgeRoute route) =>
        route.Geometry == EEdgeGeometry.Cubic || route.Geometry == EEdgeGeometry.Loop ? GraphGeometry.Flatten(route.Points).Points : route.Points;

    private static double Deviation(IReadOnlyList<GraphPoint> flat) =>
        flat.Count < 3 ? 0 : flat.Max(point => GraphGeometry.DistanceToSegment(point, flat[0], flat[^1]));

    private static double Area(IReadOnlyList<NodePlacement> members) =>
        (members.Max(card => card.Right) - members.Min(card => card.TopLeft.X)) * (members.Max(card => card.Bottom) - members.Min(card => card.TopLeft.Y));

    /// <summary>
    /// The number of relations whose straight centre-to-centre stroke passes over a card it does not join, the
    /// same clearance the written page is read with. Only the direct segment is tested: a relation the router
    /// already steers around the cards is not counted against the placement that made the detour necessary.
    /// </summary>
    internal static int MeasureBlockedRelations(GraphLayoutProfile profile, IReadOnlyList<NodePlacement> nodes)
    {
        var grow = GraphGeometryContract.CardCenterlineClearance;
        var blocked = 0;

        foreach (var (child, parent) in profile.Edges)
        {
            if (child == parent)
            {
                continue;
            }

            var from = nodes[child].Centre;
            var to = nodes[parent].Centre;
            var left = Math.Min(from.X, to.X);
            var right = Math.Max(from.X, to.X);
            var top = Math.Min(from.Y, to.Y);
            var bottom = Math.Max(from.Y, to.Y);

            foreach (var card in nodes)
            {
                if (card.NodeIndex == child || card.NodeIndex == parent
                    || card.Right + grow < left || card.TopLeft.X - grow > right
                    || card.Bottom + grow < top || card.TopLeft.Y - grow > bottom)
                {
                    continue;
                }

                if (GraphGeometry.SegmentHitsCardEnvelope(from, to, card))
                {
                    blocked++;
                    break;
                }
            }
        }

        return blocked;
    }

    /// <summary>Counts rendered strokes hidden by cards they do not join, including border-port offsets.</summary>
    internal static int MeasureHiddenRelations(
        GraphLayoutProfile profile, IReadOnlyList<NodePlacement> nodes, IReadOnlyList<EdgeRoute> routes) =>
        routes.Count(route =>
        {
            var edge = profile.Edges[route.EdgeIndex];
            var line = Flat(route);
            return edge.Child != edge.Parent && nodes.Any(card =>
                card.NodeIndex != edge.Child && card.NodeIndex != edge.Parent
                && Enumerable.Range(1, line.Count - 1).Any(at =>
                    GraphGeometry.SegmentHitsCardEnvelope(line[at - 1], line[at], card)));
        });

    /// <summary>
    /// The summed area two communities' bounding regions share, over every cluster pair inside one component.
    /// A component the clustering guards reject is absent from the partition and contributes nothing, so an
    /// ungrouped layout is never penalised for overlap it was never meant to avoid.
    /// </summary>
    internal static double MeasureClusterOverlap(GraphLayoutProfile profile, IReadOnlyList<NodePlacement> nodes, double gap = 0)
    {
        var groups = profile.ClusterGroups;
        if (groups.Count < 2)
        {
            return 0;
        }

        var envelopes = new (double Left, double Top, double Right, double Bottom)[groups.Count];
        var component = new int[groups.Count];
        for (var at = 0; at < groups.Count; at++)
        {
            var members = groups[at];
            envelopes[at] = (
                members.Min(node => nodes[node].TopLeft.X),
                members.Min(node => nodes[node].TopLeft.Y),
                members.Max(node => nodes[node].Right),
                members.Max(node => nodes[node].Bottom));
            component[at] = profile.Component[members[0]];
        }

        var total = 0d;
        for (var first = 0; first < groups.Count; first++)
        {
            for (var second = first + 1; second < groups.Count; second++)
            {
                if (component[first] != component[second])
                {
                    continue;
                }

                var width = Math.Min(envelopes[first].Right, envelopes[second].Right) - Math.Max(envelopes[first].Left, envelopes[second].Left) + gap;
                var height = Math.Min(envelopes[first].Bottom, envelopes[second].Bottom) - Math.Max(envelopes[first].Top, envelopes[second].Top) + gap;
                if (width > 0 && height > 0)
                {
                    total += width * height;
                }
            }
        }

        return total;
    }
}
