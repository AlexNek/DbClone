using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// Turns the analyzer's graph into absolute coordinates: every card is drawn in the band its references
/// put it in, and every constraint is routed without crossing a card and without running over another
/// constraint where it can help it.
/// </summary>
/// <remarks>
/// <para>
/// The engine orchestrates two passes and decides nothing itself — <see cref="GraphRankedPlacement"/>
/// places the cards and <see cref="GraphEdgeRouter"/> draws the arrows — so a placement change never
/// touches routing, and neither pass writes a string of markup (plan D12).
/// </para>
/// <para>
/// Ranked placement is a heuristic for few crossings and short arrows, not a minimum: laying a graph
/// out with minimal total edge length and no crossing is NP-hard, so nothing here claims an optimal
/// arrangement. What is promised is that the same model draws the same coordinates on every run, under
/// any culture, that a table is drawn below the tables it references, that no arrow is drawn across a
/// card it does not join, and that no two arrows are drawn along one line while another way exists
/// (T5, T25, T26).
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
                GraphRankedPlacement.Margin * 2);
        }

        var widths = new double[model.Nodes.Count];
        for (var node = 0; node < widths.Length; node++)
        {
            widths[node] = GraphCard.WidthOf(model.Nodes[node]);
        }

        var (placement, grid) = GraphRankedPlacement.Place(model, widths);
        var routes = GraphEdgeRouter.Route(model, placement, grid);

        return new CatalogGraphLayout(
            placement,
            routes,
            grid.Width,
            grid.Height);
    }
}
