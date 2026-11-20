using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;
using Xunit.Abstractions;

namespace UI.Tests.CatalogGraph;

/// <summary>Measured acceptance of named catalogs, not a universal bound for arbitrary graphs.</summary>
[Collection("Graph quality characterization")]
[Trait("Speed", "Slow")]
public sealed class GraphLayoutQualityTests(ITestOutputHelper output)
{
    public static TheoryData<string> Fixtures => new(GraphQualityFixtures.Names);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_named_fixture_is_complete_valid_and_reproducible(string fixture)
    {
        var model = GraphQualityFixtures.Model(fixture);
        var profile = Profile(model);
        var budget = new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations);
        var watch = Stopwatch.StartNew();

        var outcome = new GraphLayoutSelector().Select(profile, budget, timeLimit: TimeSpan.FromMinutes(2));

        watch.Stop();
        var validity = AssertComplete(fixture, model, profile, outcome, budget);
        var candidate = outcome.Best!;
        var signature = GraphQualitySignature.Of(candidate.Layout.Nodes, candidate.Routes, candidate.Layout.Width, candidate.Layout.Height);
        AssertReplay(fixture, signature, budget);
        var json = Snapshot(fixture, model, profile, candidate, validity, budget, watch.Elapsed);
        using var document = JsonDocument.Parse(json);
        var summary = document.RootElement.EnumerateObject().Where(property => property.Name is not ("Nodes" or "Routes" or "RouteLengths" or "RouteToDirectRatios"))
            .ToDictionary(property => property.Name, property => property.Value);
        var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(summaryJson);
        var folder = Environment.GetEnvironmentVariable("DBCLONE_GRAPH_CHARACTERIZATION");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, fixture + ".json"), json);
            File.WriteAllText(Path.Combine(folder, fixture + ".summary.json"), summaryJson);
        }

        AssertQualityBounds(fixture, document.RootElement);
    }

    private static GraphLayoutValidity AssertComplete(string fixture, CatalogGraphModel model, GraphLayoutProfile profile, GraphSelectionOutcome outcome, GraphEvaluationBudget budget)
    {
        outcome.IsSuccess.Should().BeTrue(fixture + ": " + string.Join(", ", outcome.Reports.SelectMany(report => report.Failures).Select(failure => failure.Kind)));
        outcome.IsDegraded.Should().BeFalse("quality fixtures must finish the deterministic search, not pass using the watchdog fallback");
        outcome.WasCancelled.Should().BeFalse();
        var candidate = outcome.Best!;
        candidate.Layout.Nodes.Should().HaveCount(model.Nodes.Count);
        candidate.Routes.Select(route => route.EdgeIndex).Should().Equal(Enumerable.Range(0, model.Edges.Count));
        var validity = new GraphLayoutValidator().Validate(candidate.Layout.Nodes, candidate.Routes, profile.Edges.Select(edge => new GraphEdgeEndpoints(edge.Child, edge.Parent)).ToArray());
        validity.Failures.Should().BeEmpty(fixture);
        CardOverlaps(candidate.Layout.Nodes).Should().Be(0);
        candidate.Routes.Should().OnlyContain(route => route.Geometry == EEdgeGeometry.Straight || route.Geometry == EEdgeGeometry.Cubic || route.Geometry == EEdgeGeometry.Loop);
        candidate.Routes.Should().OnlyContain(route => route.Geometry != EEdgeGeometry.Straight || route.Points.Count == 2);
        candidate.Routes.Should().OnlyContain(route => route.Shape != EEdgeShape.Line || route.Geometry != EEdgeGeometry.Loop);
        budget.Metrics.PeakRouteCandidates.Should().BeLessThanOrEqualTo(GraphSearchBudget.MaximumRouteCandidatesPerEdge);
        budget.Metrics.PeakConflictSetSize.Should().BeLessThanOrEqualTo(GraphSearchBudget.MaximumConflictSetSize);
        budget.Used.Should().BeLessThanOrEqualTo(budget.Limit);
        if (fixture == "unobstructed")
        {
            candidate.Routes.Should().OnlyContain(route => route.Geometry == EEdgeGeometry.Straight);
        }

        return validity;
    }

    private static string Snapshot(string fixture, CatalogGraphModel model, GraphLayoutProfile profile, GraphLayoutCandidate candidate, GraphLayoutValidity validity, GraphEvaluationBudget budget, TimeSpan elapsed)
    {
        var score = GraphLayoutScore.Of(candidate, profile, 0);
        var lengths = candidate.Routes.Select(route => GraphGeometry.ArcLength(Flat(route))).ToArray();
        var ratios = candidate.Routes.Where(route => !model.Edges[route.EdgeIndex].IsSelfReference)
            .Select(route => GraphGeometry.ArcLength(Flat(route)) / GraphGeometry.Distance(route.Points[0], route.Points[^1])).ToArray();
        return JsonSerializer.Serialize(new
        {
            Fixture = fixture,
            Signature = GraphQualitySignature.Of(candidate.Layout.Nodes, candidate.Routes, candidate.Layout.Width, candidate.Layout.Height),
            NodeCount = candidate.Layout.Nodes.Count,
            RouteCount = candidate.Routes.Count,
            InvalidRouteCount = validity.Failures.Select(failure => failure.EdgeIndex).Distinct().Count(),
            ForbiddenOverlapCount = validity.Failures.Count(failure => failure.Kind == ERouteFailureKind.StrokeOverlap),
            NonEndpointCardCollisionCount = validity.Failures.Where(failure => failure.Kind == ERouteFailureKind.CardCollision)
                .Sum(failure => failure.BlockingCards.Count(card => card != profile.Edges[failure.EdgeIndex].Child && card != profile.Edges[failure.EdgeIndex].Parent)),
            ShallowCrossingCount = validity.Failures.Count(failure => failure.Kind == ERouteFailureKind.ShallowCrossing),
            CardOverlapCount = CardOverlaps(candidate.Layout.Nodes),
            NormalOrthogonalPolylineCount = candidate.Routes.Count(route => route.Shape == EEdgeShape.Line && route.Geometry == EEdgeGeometry.Straight && route.Points.Count != 2),
            AdmissibleCrossings = validity.AdmissibleCrossingCount,
            score.RankViolations,
            TotalLength = lengths.Sum(),
            MaximumLength = lengths.Max(),
            MaximumRouteToDirectRatio = ratios.Max(),
            score.ComponentArea,
            score.CanvasArea,
            GeometryEvaluations = budget.Used,
            budget.Metrics.PeakRouteCandidates,
            budget.Metrics.PeakConflictSetSize,
            budget.Metrics.ConflictSets,
            budget.Metrics.RepairMoves,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            Nodes = candidate.Layout.Nodes,
            Routes = candidate.Routes,
            RouteLengths = lengths,
            RouteToDirectRatios = ratios,
        });
    }

    private static void AssertQualityBounds(string fixture, JsonElement measurements)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GraphLayoutQualityBounds.json")));
        var bounds = document.RootElement.GetProperty("hardBounds").EnumerateObject()
            .Concat(document.RootElement.GetProperty("fixtures").GetProperty(fixture).EnumerateObject());
        foreach (var bound in bounds)
        {
            var actual = measurements.GetProperty(bound.Name).GetDouble();
            double.IsFinite(actual).Should().BeTrue(fixture + ":" + bound.Name);
            actual.Should().BeLessThanOrEqualTo(bound.Value.GetDouble(), fixture + ":" + bound.Name);
        }
    }

    private static void AssertReplay(string fixture, string signature, GraphEvaluationBudget firstBudget)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var budget = new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations);
            var replay = new GraphLayoutSelector().Select(Profile(GraphQualityFixtures.Model(fixture)), budget, timeLimit: TimeSpan.FromMinutes(2));
            replay.IsSuccess.Should().BeTrue();
            replay.IsDegraded.Should().BeFalse();
            var best = replay.Best!;
            GraphQualitySignature.Of(best.Layout.Nodes, best.Routes, best.Layout.Width, best.Layout.Height).Should().Be(signature);
            budget.Used.Should().Be(firstBudget.Used, "operation counts are deterministic too");
            budget.Metrics.PeakRouteCandidates.Should().Be(firstBudget.Metrics.PeakRouteCandidates);
            budget.Metrics.PeakConflictSetSize.Should().Be(firstBudget.Metrics.PeakConflictSetSize);
            budget.Metrics.ConflictSets.Should().Be(firstBudget.Metrics.ConflictSets);
            budget.Metrics.RepairMoves.Should().Be(firstBudget.Metrics.RepairMoves);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static GraphLayoutProfile Profile(CatalogGraphModel model)
    {
        var ends = new int[model.Nodes.Count];
        foreach (var edge in model.Edges)
        {
            ends[edge.ChildIndex]++;
            ends[edge.ParentIndex]++;
        }

        var words = GraphOriginWords.For(model.Origin.Kind);
        var widths = model.Nodes.Select((node, index) => Math.Max(GraphCard.WidthOf(node, words), GraphPortAssigner.MinimumWidth(ends[index], GraphCard.Height))).ToArray();
        return GraphLayoutProfile.From(model, widths);
    }

    private static IReadOnlyList<GraphPoint> Flat(EdgeRoute route) =>
        route.Geometry == EEdgeGeometry.Straight ? route.Points : GraphGeometry.Flatten(route.Points).Points;

    private static int CardOverlaps(IReadOnlyList<NodePlacement> nodes)
    {
        var count = 0;
        for (var a = 0; a < nodes.Count; a++)
        {
            for (var b = a + 1; b < nodes.Count; b++)
            {
                if (nodes[a].TopLeft.X < nodes[b].Right && nodes[b].TopLeft.X < nodes[a].Right
                    && nodes[a].TopLeft.Y < nodes[b].Bottom && nodes[b].TopLeft.Y < nodes[a].Bottom)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
