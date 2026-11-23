using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// What the arrangement decided for one picture, in the terms the decision was made in: the seed that won,
/// what its routes cost, how large the page is, the table most relations meet, and what every other seed did.
/// A picture is written down beside these facts, so the next change is chosen from the picture that was
/// actually drawn instead of from a guess about it.
/// </summary>
/// <param name="Arrangement">The seed the chosen layout came from.</param>
/// <param name="Tables">How many tables the picture holds.</param>
/// <param name="Relations">How many relations it draws.</param>
/// <param name="Crossings">The admissible crossings of the chosen layout.</param>
/// <param name="RankViolations">Relations whose child card stands above its parent card.</param>
/// <param name="Width">The width of the arranged page.</param>
/// <param name="Height">The height of the arranged page.</param>
/// <param name="Busiest">The table the most relations meet, qualified by its schema.</param>
/// <param name="BusiestRelations">How many relation ends meet that table.</param>
/// <param name="Seeds">What every seed of every component did, in one line.</param>
/// <param name="ClusteredComponents">How many components used the clustered seed; 0 when none did.</param>
/// <param name="Clusters">The total number of clusters drawn across all clustered components.</param>
internal sealed record GraphLayoutFacts(
    string Arrangement,
    int Tables,
    int Relations,
    int Crossings,
    int RankViolations,
    double Width,
    double Height,
    string Busiest,
    int BusiestRelations,
    string Seeds,
    int ClusteredComponents,
    int Clusters)
{
    /// <summary>Takes the facts of a chosen layout from the search that chose it.</summary>
    /// <param name="chosen">The layout the search picked.</param>
    /// <param name="profile">The graph facts it was laid out from.</param>
    /// <param name="model">The catalog the picture is drawn for.</param>
    /// <param name="ends">How many relation ends meet each table, in node-index order.</param>
    /// <param name="outcome">The selection, with one report per seed of every component.</param>
    /// <returns>The facts of that picture.</returns>
    public static GraphLayoutFacts Of(
        GraphLayoutCandidate chosen,
        GraphLayoutProfile profile,
        CatalogGraphModel model,
        IReadOnlyList<int> ends,
        GraphSelectionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(ends);
        ArgumentNullException.ThrowIfNull(outcome);

        // The score is asked for rather than recomputed here, so the numbers written beside the picture are
        // the numbers it was chosen by.
        var score = GraphLayoutScore.Of(chosen, profile, 0);
        var busiest = 0;
        for (var node = 1; node < ends.Count; node++)
        {
            if (ends[node] > ends[busiest])
            {
                busiest = node;
            }
        }

        var table = model.Nodes[busiest];
        var (clusteredComponents, totalClusters) = MeasureClusters(profile, outcome.Reports);

        return new GraphLayoutFacts(
            chosen.Name,
            model.Nodes.Count,
            model.Edges.Count,
            score.Crossings,
            score.RankViolations,
            chosen.Layout.Width,
            chosen.Layout.Height,
            $"{table.Schema}.{table.Table}",
            ends[busiest],
            SeedLine(outcome.Reports),
            clusteredComponents,
            totalClusters);
    }

    private static (int ClusteredComponents, int Clusters) MeasureClusters(
        GraphLayoutProfile profile,
        IReadOnlyList<GraphSeedReport> reports)
    {
        var clusteredComponents = 0;
        var totalClusters = 0;

        var won = reports
            .Where(report => report.Name == "clustered" && report.Won)
            .Select(report => report.Component)
            .Distinct();

        foreach (var component in won)
        {
            var subgraph = profile.Subgraph(component);
            var localIndexes = Enumerable.Range(0, subgraph.Nodes.Count).ToArray();
            var localEdges = Enumerable.Range(0, subgraph.Edges.Count).ToArray();
            var clustering = GraphClusterDetector.Detect(subgraph.Profile, localIndexes, localEdges);
            if (clustering is not null)
            {
                clusteredComponents++;
                totalClusters += clustering.Members.Count;
            }
        }

        return (clusteredComponents, totalClusters);
    }

    /// <summary>
    /// Every seed of every component on one line: a valid one as the crossings and rank violations it
    /// reached, a refused one as the failures that refused it, so the seed a better picture needs is named
    /// by what stood in its way rather than by its absence.
    /// </summary>
    /// <param name="reports">One report per seed of every component.</param>
    /// <returns>The seed line.</returns>
    private static string SeedLine(IReadOnlyList<GraphSeedReport> reports) =>
        string.Join(" · ", reports.Select(report => report.Score is { } score
            ? $"{report.Name} {score.Crossings}/{score.RankViolations}"
            : $"{report.Name} refused {Kinds(report.Failures)}{Exhausted(report)}"));

    /// <summary>How many failures of each kind refused a seed, as <c>CardCollision×3</c>.</summary>
    /// <param name="failures">The seed's failures.</param>
    /// <returns>The kinds and their counts.</returns>
    private static string Kinds(IReadOnlyList<RouteFailure> failures) =>
        failures.Count == 0
            ? "nothing"
            : string.Join("+", failures.GroupBy(failure => failure.Kind).Select(group => $"{group.Key}×{group.Count()}"));

    /// <summary>The budgets that ended a seed's search, empty when none did.</summary>
    /// <param name="report">The seed's report.</param>
    /// <returns>The exhausted budgets, each named, and a space before them.</returns>
    private static string Exhausted(GraphSeedReport report) =>
        report.ExhaustedBudgets.Count == 0 ? string.Empty : $" {string.Join("+", report.ExhaustedBudgets)}";
}
