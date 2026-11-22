using System.Globalization;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;
using Xunit.Abstractions;
namespace UI.Tests.CatalogGraph;
/// <summary>
/// Refactor 17's probe (D9): each seed arrangement is forced through the selector's own path, rendered whole
/// and read from its written page. It asserts nothing, changes no comparison, budget or constant, and exists so
/// that the table it prints can be recorded before anything about placement is built.
/// </summary>
[Collection("Graph quality characterization")]
[Trait("Speed", "Slow")]
public sealed class GraphArrangementProbeTests(ITestOutputHelper output)
{
    private static readonly string[] SeedNames = ["ranked", "radial", "force", "force-wide", "force-transposed"];
    [Fact]
    public void Each_seed_arrangement_is_read_from_its_rendered_page()
    {
        var view = GraphQualityFixtures.View(GraphQualityFixtures.Catalog);
        var model = new CatalogGraphAnalyzer().Analyze(view);
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog);
        var folder = Path.Combine(Path.GetTempPath(), "dbclone-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        output.WriteLine("files: " + folder);
        for (var ordinal = 0; ordinal < SeedNames.Length; ordinal++)
        {
            var candidate = Forced(profile, ordinal);
            output.WriteLine(candidate is null ? $"{SeedNames[ordinal],-17} no complete arrangement" : Row(SeedNames[ordinal], candidate, view, model, folder));
        }
        var shipped = new GraphLayoutSelector().Select(profile).Best!;
        output.WriteLine(Row("shipped", shipped, view, model, folder));
    }
    private static GraphLayoutCandidate? Forced(GraphLayoutProfile profile, int ordinal)
    {
        var budget = new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations);
        var parts = new List<(GraphSubgraph, GraphLayoutCandidate)>();
        for (var component = 0; component < profile.ComponentNodes.Count; component++)
        {
            var sub = profile.Subgraph(component);
            var seeds = GraphDraftSeeds.Generate(sub.Profile);
            var seed = seeds[Math.Min(ordinal, seeds.Count - 1)];
            var outcome = new GraphLayoutRepair(0).Repair(seed, sub.Profile, new GraphEvaluationBudget(1000 + (300 * sub.Profile.Edges.Count)), default, hunt: false);
            if (!outcome.IsSuccess)
            {
                outcome = new GraphLayoutRepair().Repair(seed, sub.Profile, new GraphEvaluationBudget(2000 + (300 * sub.Profile.Edges.Count)), default, hunt: false);
            }
            if (!outcome.IsSuccess)
            {
                return null;
            }
            var refined = new GraphCrossingRemoval().Substitute(outcome.Candidate!, sub.Profile, budget);
            if (refined.CrossingCount != 0)
            {
                refined = new GraphLayoutRepair().ReduceCrossingsByMoving(refined, sub.Profile, budget);
            }
            parts.Add((sub, refined));
        }
        return GraphLayoutAssembler.Assemble(profile, parts, SeedNames[ordinal], budget.Used);
    }
    private static string Row(string name, GraphLayoutCandidate candidate, DbClone.Application.Relationship.RelationshipGraphView view, DbClone.UI.Models.CatalogGraph.CatalogGraphModel model, string folder)
    {
        var html = new CatalogGraphHtmlRenderer().Render(
            view,
            model,
            new CatalogGraphLayout(candidate.Layout.Nodes, candidate.Routes, candidate.Layout.Width, candidate.Layout.Height),
            DateTimeOffset.UnixEpoch);
        File.WriteAllText(Path.Combine(folder, name + ".html"), html);
        var page = GraphRenderedDocument.Parse(html);
        return string.Create(CultureInfo.InvariantCulture, $"{name,-17} {page.Line()}");
    }
}
