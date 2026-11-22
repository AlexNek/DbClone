using DbClone.UI.Services.GraphLayout;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// The measures a reader takes off a finished picture, counted the way the refactor's table states them and
/// nothing of the selector's internals: relations drawn over a card they do not join, pairs of relations
/// sharing a run, relations drawn up the page, crossing points between drawn relations, and the summed and
/// longest relation length. It reports; it never judges — a shape's thresholds are its own.
/// <see cref="RelationsOverCards"/> follows the validator and so reads zero for every chord by construction (a
/// chord is exempt from the card check); it is a curve-only measure. The geometric terms are the reader's: taken
/// from the routes and the card rectangles with the contract's own clearance, never from validator failures.
/// </summary>
/// <param name="RelationsOverCards">Relations whose stroke passes over a card they do not join.</param>
/// <param name="SharedRuns">Pairs of relations drawn along one another.</param>
/// <param name="UpThePage">Relations whose child card's centre stands above its parent's.</param>
/// <param name="CrossingPoints">Points where two drawn relations cross.</param>
/// <param name="TotalLength">The summed length of every drawn relation.</param>
/// <param name="MaximumLength">The longest single drawn relation.</param>
/// <param name="HiddenStrokes">Relations, self-references apart, whose stroke passes a card they do not join.</param>
/// <param name="OccludingCards">The distinct cards that stand over somebody else's stroke.</param>
/// <param name="BusiestFanHidden">Hidden relations among those that meet the table most relations meet.</param>
/// <param name="LabelsOnCards">Relation labels whose anchor lies strictly inside a card.</param>
internal sealed record GraphReaderMeasures(
    int RelationsOverCards,
    int SharedRuns,
    int UpThePage,
    int CrossingPoints,
    double TotalLength,
    double MaximumLength,
    int HiddenStrokes = 0,
    int OccludingCards = 0,
    int BusiestFanHidden = 0,
    int LabelsOnCards = 0)
{
    /// <summary>Reads a finished layout.</summary>
    /// <param name="candidate">The picture the reader gets.</param>
    /// <param name="profile">The graph facts.</param>
    /// <returns>The measures.</returns>
    public static GraphReaderMeasures Of(GraphLayoutCandidate candidate, GraphLayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);

        var validator = new GraphLayoutValidator();
        var lines = candidate.Routes.Select(route => Flat(route, validator)).ToArray();
        var validity = validator.Validate(
            candidate.Layout.Nodes,
            candidate.Routes,
            profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());

        var overCards = validity.Failures
            .Where(failure => failure.Kind == ERouteFailureKind.CardCollision)
            .Where(failure => failure.BlockingCards.Any(card => card != profile.Edges[failure.EdgeIndex].Child && card != profile.Edges[failure.EdgeIndex].Parent))
            .Select(failure => failure.EdgeIndex)
            .Distinct()
            .Count();
        var sharedRuns = validity.Failures
            .Where(failure => failure.Kind == ERouteFailureKind.StrokeOverlap)
            .SelectMany(failure => failure.BlockingRoutes.Select(other => (Low: Math.Min(failure.EdgeIndex, other), High: Math.Max(failure.EdgeIndex, other))))
            .Distinct()
            .Count();
        var upThePage = profile.Edges.Count(edge =>
            edge.Child != edge.Parent && candidate.Layout.Nodes[edge.Child].Centre.Y < candidate.Layout.Nodes[edge.Parent].Centre.Y);
        var lengths = lines.Select(points => GraphGeometry.ArcLength(points)).ToArray();

        return new GraphReaderMeasures(
            overCards,
            sharedRuns,
            upThePage,
            Crossings(lines),
            lengths.Sum(),
            lengths.Length == 0 ? 0 : lengths.Max())
            .WithGeometry(candidate, profile, lines);
    }

    /// <summary>Renders the measures as one line of evidence.</summary>
    /// <returns>The line.</returns>
    public string Line() =>
        $"over={RelationsOverCards} shared={SharedRuns} up={UpThePage} crossings={CrossingPoints} length={TotalLength:F0} longest={MaximumLength:F0} hidden={HiddenStrokes} occluding={OccludingCards} fan={BusiestFanHidden} labels={LabelsOnCards}";

    private GraphReaderMeasures WithGeometry(GraphLayoutCandidate candidate, GraphLayoutProfile profile, IReadOnlyList<IReadOnlyList<GraphPoint>> lines)
    {
        var nodes = candidate.Layout.Nodes;
        var meeting = new int[nodes.Count];
        foreach (var edge in profile.Edges.Where(edge => edge.Child != edge.Parent))
        {
            meeting[edge.Child]++;
            meeting[edge.Parent]++;
        }
        var busiest = meeting.Length == 0 || meeting.Max() == 0 ? -1 : Array.IndexOf(meeting, meeting.Max());
        var hidden = 0;
        var fan = 0;
        var occluders = new HashSet<int>();
        for (var route = 0; route < candidate.Routes.Count; route++)
        {
            var edge = profile.Edges[candidate.Routes[route].EdgeIndex];
            if (edge.Child == edge.Parent)
            {
                continue;
            }
            var blockers = nodes.Where(card => card.NodeIndex != edge.Child && card.NodeIndex != edge.Parent
                && Enumerable.Range(1, lines[route].Count - 1).Any(step =>
                    GraphGeometry.SegmentHitsCardEnvelope(lines[route][step - 1], lines[route][step], card))).ToArray();
            if (blockers.Length == 0)
            {
                continue;
            }
            hidden++;
            occluders.UnionWith(blockers.Select(card => card.NodeIndex));
            fan += edge.Child == busiest || edge.Parent == busiest ? 1 : 0;
        }
        var labels = candidate.Routes.Count(route => nodes.Any(card =>
            route.LabelPoint.X > card.TopLeft.X && route.LabelPoint.X < card.Right
            && route.LabelPoint.Y > card.TopLeft.Y && route.LabelPoint.Y < card.Bottom));
        return this with { HiddenStrokes = hidden, OccludingCards = occluders.Count, BusiestFanHidden = fan, LabelsOnCards = labels };
    }
    private static IReadOnlyList<GraphPoint> Flat(EdgeRoute route, GraphLayoutValidator validator) =>
        route.Geometry == EEdgeGeometry.Straight ? route.Points : validator.Flatten(route)!.Points;

    private static int Crossings(IReadOnlyList<IReadOnlyList<GraphPoint>> lines)
    {
        var crossings = 0;
        for (var first = 0; first < lines.Count; first++)
        {
            for (var second = first + 1; second < lines.Count; second++)
            {
                for (var a = 1; a < lines[first].Count; a++)
                {
                    for (var b = 1; b < lines[second].Count; b++)
                    {
                        if (GraphGeometry.SegmentsCross(lines[first][a - 1], lines[first][a], lines[second][b - 1], lines[second][b]))
                        {
                            crossings++;
                        }
                    }
                }
            }
        }

        return crossings;
    }
}
