namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The operation budgets of the layout search. They, not the clock, decide which candidates are explored,
/// so the same model gives the same picture on every machine.
/// </summary>
internal static class GraphSearchBudget
{
    /// <summary>How many draft seeds one layout request tries.</summary>
    public const int MaximumDraftSeeds = 6;

    /// <summary>How many iterations the force-directed seed runs.</summary>
    public const int MaximumForceIterations = 120;

    /// <summary>How many passes the card separator makes before it gives up on a pair.</summary>
    public const int MaximumSeparationPasses = 80;

    /// <summary>
    /// How many rounds settlements spread before the ring is ordered by them. A settlement is a group of tables
    /// that relate mostly among themselves, and every settlement holds one arc of the ring; the rounds decide
    /// that grouping, so the count is what keeps the grouping stable for the same catalog.
    /// </summary>
    public const int MaximumSettlementRounds = 8;

    /// <summary>How many route candidates one relation tries before it is reported unroutable.</summary>
    public const int MaximumRouteCandidatesPerEdge = 48;

    /// <summary>How many candidate routes one layout request may flatten and check in all.</summary>
    public const int MaximumGeometryEvaluations = 400_000;

    /// <summary>
    /// How long one layout request may search before it answers with what it has. Only an operational
    /// watchdog: it never chooses a candidate, never changes the result of a request that finishes inside the
    /// operation budgets, and never lets a partial layout out. It stops the refining rather than the request —
    /// the answer is the cheapest complete layout the remaining work can reach, flagged as degraded, because a
    /// worse picture with a warning is better than a blank view. A request with no valid layout to fall back
    /// on reports the time limit instead. The ceiling is set above the cost of one repair of a catalog-sized
    /// component on a working laptop, because a ceiling the repair only just fits turns every run into a coin
    /// toss between the routed picture and the plain one; the search still answers as soon as it has its
    /// picture, and the beauty pass is sliced by <see cref="GraphLayoutSelector"/> instead of eating the rest.
    /// </summary>
    public static readonly TimeSpan MaximumElapsedLayoutTime = TimeSpan.FromSeconds(20);

    /// <summary>How many routes one conflict set may hold.</summary>
    public const int MaximumConflictSetSize = 8;

    /// <summary>How many candidate assignments one conflict set may try.</summary>
    public const int MaximumConflictAssignments = 1500;

    /// <summary>How many candidates per relation the conflict search branches into at each level.</summary>
    public const int ConflictBranchLimit = 6;

    /// <summary>How many rip-up rounds one routing pass may run.</summary>
    public const int MaximumRipUpRounds = 6;

    /// <summary>How many placement moves one component may take during repair.</summary>
    public const int MaximumRepairMovesPerComponent = 16;

    /// <summary>How many times repair may expand the canvas of one layout.</summary>
    public const int MaximumExpansions = 4;

    /// <summary>How many repair rounds one seed may take.</summary>
    public const int MaximumRepairRounds = 60;

    /// <summary>
    /// How many repair rounds the cheap path may take. The refining search is cut short by the watchdog, so
    /// the pass that has to answer has to be able to move a table out of the way instead of reporting what it
    /// found on the first evaluation — otherwise the promise of a complete picture past the limit rests on a
    /// seed that happened to route first time.
    /// </summary>
    public const int MaximumCheapRepairRounds = 6;

    /// <summary>How many substitution passes the crossing-removal search makes over one layout.</summary>
    public const int MaximumCrossingPasses = 8;

    /// <summary>How many placement moves crossing removal may accept on one layout.</summary>
    public const int MaximumCrossingMoves = 4;

    /// <summary>How many crossings crossing removal tries placement moves for in one round.</summary>
    public const int MaximumCrossingsPerMoveRound = 3;

    /// <summary>
    /// How many placement moves the post-selection beauty pass may accept on one layout. The contest keeps the
    /// few moves that decide a shape; the beauty pass runs only once the winner is chosen, so it can spend a
    /// long descent reaching the crossings a handful of contest moves leave standing — bounded by the neighbourhood
    /// guard and its own time slice rather than by a low move count.
    /// </summary>
    public const int MaximumBeautifyCrossingMoves = 60;

    /// <summary>How many crossings the beauty pass tries placement moves for in one round.</summary>
    public const int MaximumBeautifyCrossingsPerRound = 8;

    /// <summary>The angle, in radians, one move of the radial lane reservation turns a card along its ring.</summary>
    public const double ReservationAngleStep = 0.03;
    /// <summary>How many angular steps in one direction the radial lane reservation tries for one card.</summary>
    public const int MaximumReservationSteps = 24;
    /// <summary>How many sweeps over the cards the radial lane reservation makes.</summary>
    public const int MaximumReservationRounds = 4;
    /// <summary>How many ring growths the radial lane reservation may accept in one component.</summary>
    public const int MaximumReservationGrowths = 6;

    /// <summary>
    /// The smallest connected component clustering is tried on. Below it a community split costs more in
    /// detached boxes than it buys in readable groups, so the layout keeps the ordinary contest.
    /// </summary>
    public const int MinimumClusteredComponent = 12;

    /// <summary>
    /// The modularity a community structure must reach before it is drawn as clusters. A weak split reads as
    /// the scattered picture plan §1.2 rejects, so anything below the threshold is left whole.
    /// </summary>
    public const double MinimumClusterModularity = 0.3;

    /// <summary>The smallest cluster the detector reports. Smaller ones merge into their strongest neighbour.</summary>
    public const int MinimumClusterSize = 3;

    /// <summary>
    /// The largest number of clusters a component may be split into. Above it the ring is a hairball of boxes,
    /// so the component is left whole. 300 covers any database whose tables genuinely group and still keeps
    /// the ring readable.
    /// </summary>
    public const int MaximumClusteredClusters = 300;

    /// <summary>
    /// The evaluation budget one clustered draft's per-cluster contests share. Chosen for the catalog fixture
    /// so the whole clustered draft stays inside the 2-minute Slow ceiling and inside the request's
    /// <see cref="MaximumGeometryEvaluations"/>, so the clustered draft costs a real search but never pushes a
    /// catalog-sized request into the rough fallback. It is not tuned to make clustered win.
    /// </summary>
    public const int MaximumClusterEvaluations = 60_000;

    /// <summary>The angle, in radians, one turn of the ring moves the outer clusters around the centre.</summary>
    public const double ClusterAngleStep = 0.05;

    /// <summary>The most angular steps the ring arranger tries in one direction before giving up on it.</summary>
    public const int MaximumClusterAngleSteps = 12;

    /// <summary>The most ring expansions the arranger's cross-cluster clear-ground phase tries.</summary>
    public const int MaximumClusterGrowths = 4;

    /// <summary>
    /// The most relation strokes the arranger prices crossing pairs for. Weighing a band trade means comparing
    /// every stroke with every other one, so the price grows as the square of that count while a slide only
    /// looks at the strokes of the box it moves. Past this many the trade and the crossing term of the slide
    /// are skipped and the boxes keep the order the barycentre sweeps gave them — a catalog that wide has too
    /// many relations between its groups for one swap to tell the reader anything, and the arranger must not
    /// spend the request's whole clock proving it. Below it every clustered picture is priced in full.
    /// </summary>
    public const int MaximumArrangerPricedStrokes = 400;

    /// <summary>Bounds the alternating rigid-group and within-group card placement descent.</summary>
    public const int MaximumClusterPlacementRounds = 4;

    /// <summary>Caps group, card and swap trials before their pairwise geometry work can dominate a request.</summary>
    public const int MaximumClusterPlacementTrials = 12_000;

    /// <summary>Limits all-pair group relocation to manageable community pictures.</summary>
    public const int MaximumOptimizedClusterGroups = 16;
}
