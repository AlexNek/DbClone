using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Turns the analyzer's graph into absolute coordinates, in whichever of the two arrangements draws that
/// graph's arrows cheaper: bands, which read top-down and spend the space between their cards on the
/// cards, or rings around a middle table, which leave that space empty so an arrow can go straight
/// across it. Every card is placed and every constraint is routed without crossing a card and without
/// running over another constraint where it can help it.
/// </summary>
/// <remarks>
/// <para>
/// The engine orchestrates two passes and decides nothing itself — a placement puts the cards and a
/// router draws the arrows, chosen together because the way an arrow is drawn belongs to the space the
/// arrangement leaves for it: a lattice routes in corridors, rings route straight across the empty
/// space between them (plan D12). A placement change never touches routing, and neither pass writes a
/// string of markup.
/// </para>
/// <para>
/// Ranked placement is a heuristic for few crossings and short arrows, not a minimum: laying a graph
/// out with minimal total edge length and no crossing is NP-hard, so nothing here claims an optimal
/// arrangement. What is promised is that the same model draws the same coordinates on every run, under
/// any culture, that no arrow is drawn across a card it does not join, and that no two arrows are drawn
/// along one line while another way exists. In the bands a table is also drawn below the tables it
/// references — the rule a ringed picture gives up, and the reason the bands are kept whenever they draw
/// without turning an arrow once (T5, T25, T26).
/// </para>
/// </remarks>
internal sealed class GraphLayoutEngine
{
    /// <summary>Computes every card's position and every edge's route.</summary>
    /// <param name="model">The analyzed graph, and nothing else.</param>
    /// <returns>The picture's coordinates.</returns>
    public CatalogGraphLayout Compute(CatalogGraphModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Nodes.Count == 0)
        {
            return new CatalogGraphLayout(
                [],
                [],
                GraphRankedPlacement.Margin * 2,
                GraphRankedPlacement.Margin * 2,
                false);
        }

        var words = GraphOriginWords.For(model.Origin.Kind);
        var ends = EndsPerNode(model);
        var widths = new double[model.Nodes.Count];
        for (var node = 0; node < widths.Length; node++)
        {
            // The text sets the width of a card; the arrows that meet it set a floor under that. A card
            // carrying more ends than its long sides hold would have to print two arrow heads on one
            // place, and the picture's first rule says it never does (see GraphEdgePorts.WidthFor).
            widths[node] = Math.Max(
                GraphCard.WidthOf(model.Nodes[node], words),
                GraphEdgePorts.WidthFor(ends[node], GraphCard.Height));
        }

        // Both arrangements are drawn and the cheaper one is kept. A lattice spends the space between its
        // cards on the cards, so an arrow whose two tables have a third one between them has to turn in a
        // corridor; rings leave that space open on purpose and draw the same arrow straight across it.
        // Which of the two the catalog is read in is therefore not a shape it has to match — it is counted
        // on the arrows each one actually produced (plan D12).
        var (placement, grid) = GraphRankedPlacement.Place(model, widths);
        var bands = new CatalogGraphLayout(
            placement,
            GraphEdgeRouter.Route(model, placement, grid),
            grid.Width,
            grid.Height,
            false);

        var rings = GraphWedgePlacement.Place(model, widths);
        var ringed = new CatalogGraphLayout(
            rings.Placement,
            GraphChordRouter.Route(model, rings.Placement),
            rings.Width,
            rings.Height,
            true);

        // The bands stand unless the ringed picture measures cheaper, so a catalog whose arrows already go
        // straight down from every parent to its children is not moved into rings for the sake of moving
        // it, and the same catalog settles the same way on every run.
        return GraphDrawingCost.MeasuresBetterThan(
            GraphDrawingCost.Of(model, ringed.Nodes, ringed.Edges),
            GraphDrawingCost.Of(model, bands.Nodes, bands.Edges))
            ? ringed
            : bands;
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
