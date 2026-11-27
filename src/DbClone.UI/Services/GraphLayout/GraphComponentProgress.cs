namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// What one component's search has reached so far. It outlives a search that the elapsed-time watchdog cuts
/// short, so the fallback resumes with the seeds it already generated, the probes it already paid for and the
/// best complete layout it already had, instead of paying for all of them again.
/// </summary>
internal sealed class GraphComponentProgress
{
    /// <summary>Gets the component's seeds, generated once.</summary>
    public List<GraphDraftLayout> Seeds { get; } = [];

    /// <summary>Gets one probe outcome per seed, in seed order; empty until the first probe pass runs.</summary>
    public List<(int Ordinal, GraphRepairOutcome Outcome, int Spent)> Probes { get; } = [];

    /// <summary>Gets or sets the best complete layout the search has reached.</summary>
    public GraphLayoutCandidate? Best { get; set; }

    /// <summary>Gets or sets the score of <see cref="Best"/>.</summary>        
    public GraphLayoutScore? BestScore { get; set; }

    /// <summary>
    /// Gets or sets which seed came nearest to complete, by the order <see cref="GraphDraftSeeds"/> makes them
    /// in. The seed's own draft belongs to this component alone, so only its place in that order is kept: the
    /// last-resort picture is a whole-graph draft and is asked for from the whole graph.
    /// </summary>
    public int NearestOrdinal { get; set; } = -1;

    /// <summary>Gets or sets how many relations that seed could not route.</summary>
    public int NearestFailures { get; set; } = int.MaxValue;

    /// <summary>
    /// Gets or sets whether one of this component's clock-sliced passes — the refining search or the
    /// post-selection beauty pass — gave up mid-work because its own slice expired while the request as a whole
    /// stayed inside <see cref="GraphSearchBudget.MaximumElapsedLayoutTime"/>. Which improvements a slice leaves
    /// untried depends on the machine, so the operation counts of such a request are not a reproducible quantity
    /// even when its picture is.
    /// </summary>
    public bool WasSliceCut { get; set; }
}
