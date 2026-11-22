namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The operation budgets of the layout search. They, not the clock, decide which candidates are explored,
/// so the same model gives the same picture on every machine.
/// </summary>
internal static class GraphSearchBudget
{
    /// <summary>How many draft seeds one layout request tries.</summary>
    public const int MaximumDraftSeeds = 5;

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
    /// <summary>The angle, in radians, one move of the radial lane reservation turns a card along its ring.</summary>
    public const double ReservationAngleStep = 0.03;
    /// <summary>How many angular steps in one direction the radial lane reservation tries for one card.</summary>
    public const int MaximumReservationSteps = 24;
    /// <summary>How many sweeps over the cards the radial lane reservation makes.</summary>
    public const int MaximumReservationRounds = 4;
    /// <summary>How many ring growths the radial lane reservation may accept in one component.</summary>
    public const int MaximumReservationGrowths = 6;
}