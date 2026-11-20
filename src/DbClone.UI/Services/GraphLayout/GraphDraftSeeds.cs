namespace DbClone.UI.Services.GraphLayout;

/// <summary>Generates the bounded, ordered set of draft seeds one layout request tries.</summary>
internal static class GraphDraftSeeds
{
    /// <summary>Generates the seeds: ranked, radial, force-directed, wider force-directed, transposed force-directed.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="cancellation">Checked between seeds and force iterations.</param>
    /// <param name="maximum">How many seeds to make; the cheap path asks for the ranked one alone.</param>
    /// <returns>At most <paramref name="maximum"/> drafts, in a fixed order.</returns>
    public static IReadOnlyList<GraphDraftLayout> Generate(
        GraphLayoutProfile profile,
        CancellationToken cancellation = default,
        int maximum = GraphSearchBudget.MaximumDraftSeeds)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var makers = new List<Func<GraphDraftLayout>>
        {
            () => GraphRankedSeed.Place(profile),
            () => GraphRadialSeed.Place(profile),
            () => GraphForceSeed.Place(profile, GraphForceVariant.Standard, cancellation),
            () => GraphForceSeed.Place(profile, GraphForceVariant.Wide, cancellation),
            () => GraphForceSeed.Place(profile, GraphForceVariant.Transposed90, cancellation),
        };

        var drafts = new List<GraphDraftLayout>();
        foreach (var make in makers.Take(Math.Clamp(maximum, 1, makers.Count)))
        {
            cancellation.ThrowIfCancellationRequested();
            drafts.Add(make());
        }

        return drafts;
    }
}
