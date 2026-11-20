namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The soft ordering of two valid layouts, compared field by field in the order declared and never as a
/// weighted sum, so no count can buy a relation drawn up the page and a short route can never buy a
/// crossing. The hard validity every layout must have first is not a field: a layout with any hard failure
/// is not scored at all.
/// </summary>
/// <param name="Crossings">The admissible crossings; zero is the target.</param>
/// <param name="RankViolations">Relations whose child card's centre stands above its parent's.</param>
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
        var violations = profile.Edges.Count(edge =>
            edge.Child != edge.Parent
            && !(profile.Group[edge.Child] == profile.Group[edge.Parent] && profile.GroupSize[edge.Child] > 1)
            && nodes[edge.Child].Centre.Y < nodes[edge.Parent].Centre.Y - Tolerance);
        var distance = profile.Edges.Count == 0
            ? 0
            : profile.Edges.Max(edge => GraphGeometry.Distance(nodes[edge.Child].Centre, nodes[edge.Parent].Centre));

        return new GraphLayoutScore(
            candidate.CrossingCount,
            violations,
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

    private static IReadOnlyList<GraphPoint> Flat(EdgeRoute route) =>
        route.Geometry == EEdgeGeometry.Cubic || route.Geometry == EEdgeGeometry.Loop ? GraphGeometry.Flatten(route.Points).Points : route.Points;

    private static double Deviation(IReadOnlyList<GraphPoint> flat) =>
        flat.Count < 3 ? 0 : flat.Max(point => GraphGeometry.DistanceToSegment(point, flat[0], flat[^1]));

    private static double Area(IReadOnlyList<NodePlacement> members) =>
        (members.Max(card => card.Right) - members.Min(card => card.TopLeft.X)) * (members.Max(card => card.Bottom) - members.Min(card => card.TopLeft.Y));
}
