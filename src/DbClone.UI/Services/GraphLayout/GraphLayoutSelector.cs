using System.Diagnostics;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Candidate selection. Each connected component is laid out on its own: every seed is repaired into a
/// complete valid layout, its crossings are reduced by route substitution and then by placement moves, and
/// the valid layouts compete on <see cref="GraphLayoutScore"/> alone. The best layout of every component is
/// then assembled into one picture. A seed that cannot be repaired is discarded and reported; a component
/// with no valid layout makes the whole request return the reasons, never a picture with a hard failure. A
/// cancelled request returns no layout at all, however many components had finished. The elapsed-time
/// watchdog ends the search rather than the picture: it stops the refining, lays the components it has not
/// reached on the cheapest path that still yields a complete valid layout, and answers with that layout and
/// <see cref="GraphSelectionOutcome.IsDegraded"/> — a worse picture with a warning, never a blank view and
/// never partial geometry.
/// </summary>
internal sealed class GraphLayoutSelector
{
    private const int ProbeBaseBudget = 200;

    /// <summary>
    /// What one probe may spend per relation: one candidate, the one the shape itself offers. A probe asks
    /// whether a shape suits the catalog at all, and its answer is the failure count the repair is then ordered
    /// by — not a routed picture. A probe that spends the repair's share of the clock proving that costs the
    /// shape that came nearest the repair it needs, and a probe of one candidate per relation already tells the
    /// shapes apart: on a catalog of settlements the seeds fail on twenty-seven to sixty-two relations of
    /// ninety-one, and the nearest one is the only seed that repairs.
    /// </summary>
    private const int ProbeBudgetPerEdge = 1;

    private const int SeedBaseBudget = 2000;
    private const int SeedBudgetPerEdge = 300;

    /// <summary>How many incomplete seeds of one component get the full repair search.</summary>
    private const int MaximumRepairedSeeds = 2;
    private const int RefineBaseBudget = 1000;
    private const int RefineBudgetPerEdge = 300;

    /// <summary>
    /// The most clock the beauty pass may take from one component's request. The refining lowers crossings and
    /// never makes a picture valid, so it is spent like a tip: a share of what the picture left over, capped so
    /// a catalog-sized component cannot turn a search that already has its answer into a full-length wait. A
    /// substitution pass over a large catalog takes far longer than any tip a component has left, so the share
    /// is a quarter and the cap two seconds: the pass that cannot finish is not worth the reader's wait, and the
    /// catalogs that do gain from it reach their better picture well inside the slice.
    /// </summary>
    private static readonly TimeSpan MaximumRefineTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many candidate routes the cheap path's repair of one seed may check, however many relations it holds.
    /// The watchdog has already spent the search's time when this path is reached, so the repair is capped by an
    /// operation count instead of by the clock that put it here.
    /// </summary>
    private const int MaximumCheapEvaluations = 12_000;

    private readonly GraphLayoutRepair _repair = new();
    private readonly GraphLayoutRepair _probe = new(0);

    /// <summary>
    /// The pass that answers once the watchdog has fired. Unlike the probe it may move a table out of the way,
    /// because its picture is the one the reader gets: a seed that did not route first time must still be
    /// repaired into a complete arrangement rather than reported as a failure.
    /// </summary>
    private readonly GraphLayoutRepair _cheap = new(GraphSearchBudget.MaximumCheapRepairRounds);
    private readonly GraphCrossingRemoval _removal = new();

    /// <summary>Chooses the best valid layout.</summary>
    /// <param name="profile">The graph facts.</param>
    /// <param name="budget">The shared evaluation budget; a fresh one when omitted.</param>
    /// <param name="cancellation">Checked between seeds and inside every search.</param>
    /// <param name="timeLimit">
    /// The watchdog; <see cref="GraphSearchBudget.MaximumElapsedLayoutTime"/> when omitted. It bounds the
    /// search, not the answer: past it no seed is refined and every component still gets its cheapest complete
    /// layout, so a limit of zero returns the plain seed arrangement instead of nothing.
    /// </param>
    /// <returns>The chosen layout and one report per seed of every component.</returns>
    public GraphSelectionOutcome Select(
        GraphLayoutProfile profile,
        GraphEvaluationBudget? budget = null,
        CancellationToken cancellation = default,
        TimeSpan? timeLimit = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var limit = timeLimit ?? GraphSearchBudget.MaximumElapsedLayoutTime;
        if (limit < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeLimit), "The time limit must not be negative.");
        }

        budget ??= new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations);
        var reports = new List<GraphSeedReport>();
        var parts = new List<(GraphSubgraph, GraphLayoutCandidate)>();
        var watch = Stopwatch.StartNew();
        var degraded = false;
        var failed = false;
        var nearest = -1;
        var nearestFailures = int.MaxValue;

        // The watchdog rides the caller's token, so every phase boundary and every inner search that already
        // honours cancellation honours the time limit as well. Once it fires the caller's token alone is
        // handed down: the remaining work is the cheap path, not a cancellation.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        watchdog.CancelAfter(limit);

        try
        {
            for (var component = 0; component < profile.ComponentNodes.Count; component++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (watch.Elapsed >= limit)
                {
                    degraded = true;
                }

                var sub = profile.Subgraph(component);
                var progress = new GraphComponentProgress();
                GraphLayoutCandidate? best;
                try
                {
                    best = SolveComponent(sub, component, budget, reports, progress, degraded ? cancellation : watchdog.Token, !degraded, Left(watch, limit));
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // The limit fired inside this component's search: it resumes on the cheap path with the seeds
                    // it generated, the probes it paid for and the best layout it already had, so the picture
                    // keeps every table instead of losing the component it was working on.
                    degraded = true;
                    reports.RemoveAll(report => report.Component == component);
                    best = SolveComponent(sub, component, budget, reports, progress, cancellation, refine: false, TimeSpan.Zero);
                }

                if (progress.NearestFailures < nearestFailures)
                {
                    (nearest, nearestFailures) = (progress.NearestOrdinal, progress.NearestFailures);
                }

                if (best is null)
                {
                    failed = true;
                    continue;
                }

                parts.Add((sub, best));
            }
        }
        catch (OperationCanceledException)
        {
            return new GraphSelectionOutcome(null, reports, budget.Used, true, false);
        }

        if (failed)
        {
            // The caller's cancellation is answered the same way here as everywhere else: no picture at all.
            if (cancellation.IsCancellationRequested)
            {
                return new GraphSelectionOutcome(null, reports, budget.Used, true, false);
            }

            // A component no seed could finish would otherwise cost the reader the whole picture, which is the
            // outcome the owner rejected. The whole graph is drawn with one straight stretch per relation and
            // reported as rough, in the arrangement that came nearest — a ring stays a ring around the table
            // many tables name — so the warning is beside the picture the search believed in most.
            // A component's seeds are its own; the last-resort picture covers the whole graph, so the shape is
            // asked for from the whole graph by the place the seed holds in the fixed seed order.
            var plain = nearest < 0
                ? GraphRoughLayout.Of(profile)
                : GraphRoughLayout.Of(profile, GraphDraftSeeds.Generate(profile, default, nearest + 1)[nearest]);

            return new GraphSelectionOutcome(plain, reports, budget.Used, false, degraded, IsRough: true);
        }

        var assembled = GraphLayoutAssembler.Assemble(profile, parts, parts.Count == 1 ? parts[0].Item2.Name : "assembled", budget.Used);

        // Assembly does not observe the token, so the caller is asked once more: a cancellation that landed
        // during it still means no picture, exactly as it would anywhere else in the request.
        if (cancellation.IsCancellationRequested)
        {
            return new GraphSelectionOutcome(null, reports, budget.Used, true, false);
        }

        return new GraphSelectionOutcome(assembled, reports, budget.Used, false, degraded);
    }

    private GraphLayoutCandidate? SolveComponent(
        GraphSubgraph sub,
        int component,
        GraphEvaluationBudget budget,
        List<GraphSeedReport> reports,
        GraphComponentProgress progress,
        CancellationToken cancellation,
        bool refine,
        TimeSpan left)
    {
        var profile = sub.Profile;

        if (progress.Seeds.Count == 0)
        {
            // The refining search compares the whole seed set; the cheap path has nothing left to compare with,
            // so it makes the plain ranked arrangement alone — the one seed that costs no force iteration.
            var generated = GraphDraftSeeds.Generate(profile, cancellation, refine ? GraphSearchBudget.MaximumDraftSeeds : 1);

            // One card has one arrangement: the other seeds would draw the same thing four more times.
            progress.Seeds.AddRange(profile.NodeCount == 1 ? generated.Take(1) : generated);
        }

        var seeds = progress.Seeds;

        // Phase one routes every seed once, with one candidate per relation and no placement repair: the probe
        // asks whether a shape suits the catalog, not whether rip-up can rescue it, so it costs a fraction of a
        // second and leaves the clock to the repair. A pass resumed after a watchdog keeps the probes it already
        // paid for.
        if (progress.Probes.Count == 0)
        {
            for (var ordinal = 0; ordinal < seeds.Count; ordinal++)
            {
                cancellation.ThrowIfCancellationRequested();
                var probeBudget = Child(budget, ProbeBaseBudget + (ProbeBudgetPerEdge * profile.Edges.Count));
                var outcome = _probe.Repair(seeds[ordinal], profile, probeBudget, cancellation, hunt: false);
                budget.Add(probeBudget.Used);
                if (outcome.WasCancelled)
                {
                    throw new OperationCanceledException(cancellation);
                }

                progress.Probes.Add((ordinal, outcome, probeBudget.Used));

                // The cheap path stops at the first seed that routes: its picture is one arrangement, not a
                // contest among five, and the relations it could not place are reported only when none routes.
                if (!refine && outcome.IsSuccess)
                {
                    break;
                }
            }
        }

        var probed = progress.Probes;

        // Phase two repairs the seeds that came nearest first: the probes are the order, because the shape that
        // kept most of its relations is the one worth the repair. The refining search spends the full repair, and
        // only on the two nearest, and nothing at all when a seed is complete already. The cheap path has no clock
        // left and a short repair, but it asks the same question in the same order and answers as soon as one seed
        // yields a picture: a pass that gave up on the first seed in draft order lost the picture that the seed
        // beside it could have drawn. Both stop at the first seed they complete — the reader gets one arrangement,
        // and the clock it takes to prove a second one is the clock that was missing when the last repair failed.
        var anyComplete = probed.Any(item => item.Outcome.IsSuccess);
        var nearestFirst = probed
            .Where(item => !item.Outcome.IsSuccess && !(refine && anyComplete))
            .OrderBy(item => item.Outcome.Failures.Count).ThenBy(item => item.Ordinal)
            .ToList();
        var repairs = (refine ? nearestFirst.Take(MaximumRepairedSeeds) : nearestFirst)
            .Select((item, rank) => (item.Ordinal, Rank: rank))
            .ToDictionary(item => item.Ordinal, item => item.Rank);

        var best = progress.Best;
        var bestScore = progress.BestScore;
        var found = new List<(int Ordinal, GraphSeedReport Report)>();

        foreach (var (ordinal, first, probeSpent) in probed.OrderBy(item => Rank(item.Ordinal)))
        {
            var outcome = first;
            var spent = probeSpent;
            var attempted = !first.IsSuccess && repairs.ContainsKey(ordinal) && progress.Best is null;
            if (attempted)
            {
                var repairBudget = Child(
                    budget,
                    refine ? SeedBaseBudget + (SeedBudgetPerEdge * profile.Edges.Count) : MaximumCheapEvaluations);
                outcome = (refine ? _repair : _cheap).Repair(seeds[ordinal], profile, repairBudget, cancellation, hunt: false);
                budget.Add(repairBudget.Used);
                spent += repairBudget.Used;
                if (outcome.WasCancelled)
                {
                    throw new OperationCanceledException(cancellation);
                }
            }

            if (!outcome.IsSuccess)
            {
                // The last-resort picture is drawn in the arrangement that came nearest, so the shape the
                // search believed in most survives the report that no arrangement was proven.
                if (outcome.Failures.Count < progress.NearestFailures)
                {
                    (progress.NearestOrdinal, progress.NearestFailures) = (ordinal, outcome.Failures.Count);
                }

                var exhausted = attempted ? outcome.ExhaustedBudgets : outcome.ExhaustedBudgets.Append(nameof(MaximumRepairedSeeds)).ToArray();
                found.Add((ordinal, new GraphSeedReport(seeds[ordinal].Name, null, outcome.Failures.Select(failure => Global(failure, sub)).ToArray(), exhausted, spent, component)));
                continue;
            }

            // The seed's own layout is scored before refining can interrupt it, so a watchdog never costs the
            // component the complete candidate it already had.
            var chosen = outcome.Candidate!;
            var score = Consider(chosen, ordinal);

            if (refine)
            {
                var refineBudget = Child(budget, RefineBaseBudget + (RefineBudgetPerEdge * profile.Edges.Count));
                using var slice = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                slice.CancelAfter(RefineSlice(left));
                try
                {
                    chosen = Refine(chosen, profile, refineBudget, slice.Token);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // The beauty pass was cut by its own slice, not by the caller: the complete arrangement it
                    // started from is the answer, so the picture keeps its validity and the request says nothing
                    // it has to apologise for. Crossings are a reading cost; a relation drawn through a card is
                    // a lost relation, and only the repair may decide that.
                }
                finally
                {
                    budget.Add(refineBudget.Used);
                }

                spent += refineBudget.Used;
                score = Consider(chosen, ordinal);
            }

            found.Add((ordinal, new GraphSeedReport(seeds[ordinal].Name, score, [], [], spent, component)));
        }

        // The report is one entry per seed in seed order, whatever order the repairs were tried in.
        reports.AddRange(found.OrderBy(item => item.Ordinal).Select(item => item.Report));

        return best;

        // Where a seed stands in the repair order; the seeds that are not repaired are visited last, after the
        // ones the order names, so a complete seed is scored as soon as the clock would allow it.
        int Rank(int ordinal) => repairs.TryGetValue(ordinal, out var rank) ? rank : repairs.Count;

        // Scores a candidate and takes it when it beats what the search has, keeping the progress in step with
        // the local best so an interrupted pass resumes where it stopped.
        GraphLayoutScore Consider(GraphLayoutCandidate candidate, int ordinal)
        {
            var score = GraphLayoutScore.Of(candidate, profile, ordinal);
            if (bestScore is null || score.CompareTo(bestScore.Value) < 0)
            {
                (best, bestScore) = (candidate, score);
                (progress.Best, progress.BestScore) = (candidate, score);
            }

            return score;
        }
    }

    /// <summary>A budget of its own for one search, never larger than what the request has left.</summary>
    private static GraphEvaluationBudget Child(GraphEvaluationBudget budget, int wanted) =>
        new(Math.Max(0, Math.Min(budget.Limit - budget.Used, wanted)), budget.Metrics);

    /// <summary>What the request's watchdog has left for the component being worked on.</summary>
    /// <param name="watch">The request's own clock.</param>
    /// <param name="limit">The watchdog's limit.</param>
    /// <returns>The time left, which is nothing once the limit has been reached.</returns>
    private static TimeSpan Left(Stopwatch watch, TimeSpan limit) =>
        watch.Elapsed >= limit ? TimeSpan.Zero : limit - watch.Elapsed;

    /// <summary>The beauty pass's share of the time the component's picture left over.</summary>
    /// <param name="left">What the watchdog still allows.</param>
    /// <returns>The slice: a quarter of the time left, never larger than <see cref="MaximumRefineTime"/>.</returns>
    private static TimeSpan RefineSlice(TimeSpan left) =>
        left <= TimeSpan.Zero ? TimeSpan.Zero
            : left / 4 <= MaximumRefineTime ? left / 4
            : MaximumRefineTime;

    /// <summary>A failure expressed in the whole graph's node and edge indexes.</summary>
    private static RouteFailure Global(RouteFailure failure, GraphSubgraph sub) =>
        new(
            failure.EdgeIndex >= 0 && failure.EdgeIndex < sub.Edges.Count ? sub.Edges[failure.EdgeIndex] : failure.EdgeIndex,
            failure.Kind,
            failure.BlockingCards.Select(card => sub.Nodes[card]).ToArray(),
            failure.BlockingRoutes.Select(edge => sub.Edges[edge]).ToArray(),
            failure.ExploredCandidates);

    /// <summary>Reduces the crossings of a valid layout: by substitution, then by moving a table.</summary>
    private GraphLayoutCandidate Refine(GraphLayoutCandidate candidate, GraphLayoutProfile profile, GraphEvaluationBudget budget, CancellationToken cancellation)
    {
        var current = _removal.Substitute(candidate, profile, budget, cancellation);

        return current.CrossingCount == 0 ? current : _repair.ReduceCrossingsByMoving(current, profile, budget, cancellation);
    }
}
