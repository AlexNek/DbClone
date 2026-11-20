using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Turns the analyzer's graph into a picture: every card placed, every relation routed, and the coordinates
/// absolute in the document's own space. The arrangement is chosen on the arrows it actually produced, so a
/// catalog whose relations already read top-down keeps that reading and a blocked relation is a curve around
/// the card in its way instead of a dog-leg.
/// </summary>
/// <remarks>
/// <para>
/// The engine is the orchestration boundary and nothing more: it sizes the cards, hands the facts to
/// <see cref="GraphLayoutSelector"/> and translates the chosen candidate into the coordinates the writers
/// consume. Placement, ports, routing, validation and scoring live in the components it calls, so a change to
/// any of them is invisible here.
/// </para>
/// <para>
/// What leaves this class is either a complete, valid picture or the explicit reason there is none. It is
/// never a partial one, and the elapsed-time watchdog never empties the answer: a search it cut short still
/// reports the cheapest complete arrangement it could reach, flagged as degraded.
/// </para>
/// </remarks>
internal sealed class GraphLayoutEngine
{
    /// <summary>Computes every card's position and every edge's route.</summary>
    /// <param name="model">The analyzed graph, and nothing else.</param>
    /// <param name="cancellation">The caller's own cancellation; it ends the request rather than degrading it.</param>
    /// <param name="timeLimit">
    /// The watchdog, <see cref="GraphSearchBudget.MaximumElapsedLayoutTime"/> when omitted. It bounds the search
    /// and not the picture: past it the answer is the cheapest complete arrangement, flagged degraded.
    /// </param>
    /// <returns>The picture, or the reason there is none.</returns>
    public GraphLayoutResult Compute(
        CatalogGraphModel model,
        CancellationToken cancellation = default,
        TimeSpan? timeLimit = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Nodes.Count == 0)
        {
            return GraphLayoutResult.Success(
                new CatalogGraphLayout(
                    [],
                    [],
                    GraphPlacementConstants.Margin * 2,
                    GraphPlacementConstants.Margin * 2));
        }

        var words = GraphOriginWords.For(model.Origin.Kind);
        var ends = EndsPerNode(model);
        var widths = new double[model.Nodes.Count];
        for (var node = 0; node < widths.Length; node++)
        {
            // The text sets the width of a card; the arrows that meet it set a floor under that. A card
            // carrying more ends than its long sides hold would have to print two arrow heads on one
            // place, and the picture's first rule says it never does (see GraphPortAssigner.MinimumWidth).
            widths[node] = Math.Max(
                GraphCard.WidthOf(model.Nodes[node], words),
                GraphPortAssigner.MinimumWidth(ends[node], GraphCard.Height));
        }

        var profile = GraphLayoutProfile.From(model, widths);
        var outcome = new GraphLayoutSelector().Select(profile, cancellation: cancellation, timeLimit: timeLimit);

        if (outcome.Best is not { } best)
        {
            // The caller's cancellation is not the machine running out of time, and the workflow says which
            // of the two happened beside the picture it could not replace.
            var kind = outcome.WasCancelled
                ? ELayoutFailureKind.Cancelled
                : outcome.WasTimedOut ? ELayoutFailureKind.LayoutTimeLimit : ELayoutFailureKind.NoValidLayout;

            return GraphLayoutResult.Failed(new GraphLayoutFailure(kind, outcome.Reports, outcome.GeometryEvaluations));
        }

        return GraphLayoutResult.Success(
            new CatalogGraphLayout(best.Layout.Nodes, best.Routes, best.Layout.Width, best.Layout.Height),
            outcome.IsDegraded,
            outcome.IsRough,
            GraphLayoutFacts.Of(best, profile, model, ends, outcome));
    }

    /// <summary>How many arrow ends meet each card, counted as the border is dealt out to them.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <returns>One count per node, in node-index order.</returns>
    private static int[] EndsPerNode(CatalogGraphModel model)
    {
        var ends = new int[model.Nodes.Count];

        foreach (var edge in model.Edges)
        {
            ends[edge.ChildIndex]++;
            ends[edge.ParentIndex]++;
        }

        return ends;
    }
}
