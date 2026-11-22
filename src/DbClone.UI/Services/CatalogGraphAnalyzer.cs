using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;

namespace DbClone.UI.Services;

/// <summary>
/// The analysis stage: turns a catalog's captured relationship facts into the picture's own model,
/// and decides everything the later stages would otherwise have to decide again.
/// </summary>
/// <remarks>
/// <para>
/// This class is the stage's coordinator and nothing else: it runs the parts in order and assembles
/// their answers. Each decision has its own class — the node set, the edges and their texts, the
/// degrees, the cyclic groups, the cluster chips, the opening view (D8), the details and the search
/// index — so a rule can change in one place and every consumer of it stays in step (D12).
/// </para>
/// <para>
/// The size decision below is a product default stated apart from any readability claim: how large a
/// cyclic group may grow before it is drawn behind a supernode. It is a constant here so a test
/// asserts the rule and not a pixel. No such default narrows the first picture: every table the model
/// holds opens on it, and only a table the reader selected narrows it.
/// </para>
/// <para>
/// Nothing is ever keyed, merged, looked up or drawn by display text. A node's identity is the
/// catalog's own reference, compared ordinally, so two references that state no name stay two
/// nodes and one name in two schemas stays two schemas.
/// </para>
/// </remarks>
internal sealed class CatalogGraphAnalyzer
{
    /// <summary>The member count above which a cyclic group opens behind a supernode.</summary>
    internal const int CollapsedComponentSizeLimit = 8;

    /// <summary>The words for a constraint that captured no name.</summary>
    internal const string UnnamedConstraintLabel = "(unnamed constraint)";

    /// <summary>The words on the chip that claims every table with no relation at all.</summary>
    internal const string UnconnectedLabel = "unconnected";

    /// <summary>Analyzes one catalog's relationship view.</summary>
    /// <param name="view">The facts read from the catalog folder.</param>
    /// <param name="preferredFocusId">
    /// The node the reader already selected, when there is one. It is the only thing that narrows the
    /// opening view: the picture then opens on this table and its neighbours instead of on the whole
    /// database.
    /// </param>
    /// <returns>The model every later stage reads.</returns>
    public CatalogGraphModel Analyze(RelationshipGraphView view, string? preferredFocusId = null)
    {
        ArgumentNullException.ThrowIfNull(view);

        var seeds = GraphNodeSeeds.Order(view);
        var indexById = GraphNodeSeeds.Index(seeds);
        var edges = GraphEdgeBuilder.Build(view, indexById, seeds);
        var joinOfNode = GraphNodeSeeds.JoinTables(seeds, edges);
        var degrees = GraphDegrees.Measure(seeds.Count, edges);
        var component = StronglyConnectedComponents.Compute(
            seeds.Count,
            [.. edges.Select(edge => (edge.ChildIndex, edge.ParentIndex))]);
        var groups = GraphCyclicGroups.Build(seeds, component);
        var groupOfNode = GraphCyclicGroups.GroupOfNode(groups, seeds.Count);
        var neighbours = GraphNeighbourhoods.Measure(seeds.Count, edges);
        var chips = GraphClusterChips.Build(view, seeds, groups, degrees, out var chipsByNode);
        var initialView = GraphInitialViewPlanner.Plan(
            seeds,
            neighbours,
            indexById,
            preferredFocusId);

        return new CatalogGraphModel(
            GraphNodeSeeds.Number(seeds, degrees, groupOfNode, joinOfNode),
            indexById,
            edges,
            GraphDetailBuilder.Build(seeds, edges, neighbours, groups, groupOfNode),
            chips,
            groups,
            chipsByNode,
            initialView,
            GraphSearchIndexBuilder.Build(seeds, edges),
            seeds.Count(seed => seed.IsCaptured),
            edges.Count,
            view.HasData,
            view.Origin);
    }
}
