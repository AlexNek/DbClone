namespace DbClone.UI.Services.GraphLayout;

/// <summary>Generates the bounded, ordered set of draft seeds one layout request tries.</summary>
internal static class GraphDraftSeeds
{
    /// <summary>Generates the seeds: ranked, radial, force-directed, wider force-directed, transposed force-directed, clustered.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="cancellation">Checked between seeds and force iterations.</param>
    /// <param name="maximum">How many seeds to make; the cheap path asks for the ranked one alone.</param>
    /// <param name="budget">The shared evaluation budget passed to the clustered seed; the request's standard
    /// cap when omitted, so a clustered maker never spends outside a budget it cannot report.</param>
    /// <param name="timeLeft">Reports the time remaining for the clustered seed; null when not needed.</param>
    /// <param name="clustering">When false, the clustered maker is not generated (nested per-cluster contests).</param>
    /// <returns>At most <paramref name="maximum"/> drafts, in a fixed order.</returns>
    public static IReadOnlyList<GraphDraftLayout> Generate(
        GraphLayoutProfile profile,
        CancellationToken cancellation = default,
        int maximum = GraphSearchBudget.MaximumDraftSeeds,
        GraphEvaluationBudget? budget = null,
        Func<TimeSpan>? timeLeft = null,
        bool clustering = true)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var makers = new List<Func<GraphDraftLayout?>>
        {
            () => GraphRankedSeed.Place(profile),
            () => GraphRadialSeed.Place(profile),
            () => GraphForceSeed.Place(profile, GraphForceVariant.Standard, cancellation),
            () => GraphForceSeed.Place(profile, GraphForceVariant.Wide, cancellation),
            () => GraphForceSeed.Place(profile, GraphForceVariant.Transposed90, cancellation),
        };

        if (clustering)
        {
            makers.Add(() => GraphClusteredSeed.Place(
                profile,
                cancellation,
                budget ?? new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations),
                timeLeft));
        }

        var drafts = new List<GraphDraftLayout>();
        foreach (var make in makers.Take(Math.Clamp(maximum, 1, makers.Count)))
        {
            cancellation.ThrowIfCancellationRequested();
            var draft = make();
            if (draft is not null)
            {
                drafts.Add(draft);
            }
        }

        return drafts;
    }
}
