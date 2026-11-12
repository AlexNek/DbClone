using DbClone.Application.Relationship;

namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// The presentation-independent graph the analyzer produces and every later stage reads. The
/// renderer formats this model and derives nothing of its own: a relationship, a name and a match list
/// all arrive here already decided.
/// </summary>
/// <param name="Nodes">Every node — captured and outside — in canonical order.</param>
/// <param name="NodeIndexesById">
/// The identity lookup: a catalog reference to the node it names. Nothing is ever found by display
/// text.
/// </param>
/// <param name="Edges">One entry per captured constraint, in canonical order.</param>
/// <param name="DetailsByNode">The panel record for each node, keyed by node index.</param>
/// <param name="Chips">The cluster chips, in bar order: schemas, then cyclic groups, then the unconnected block.</param>
/// <param name="CyclicGroups">The groups of two or more tables that reach each other, in group order.</param>
/// <param name="ChipIndexesByNode">For each node, the chips that claim it. A node may be claimed by several.</param>
/// <param name="InitialView">What the picture shows when it opens.</param>
/// <param name="SearchIndex">The searchable facts, in canonical order.</param>
/// <param name="TableCount">How many tables the catalog holds.</param>
/// <param name="RelationCount">How many constraints the catalog holds — the register's own total.</param>
/// <param name="HasData">Whether the origin states any row count at all.</param>
/// <param name="Origin">Where the graph was read from: the words the document prints follow its kind.</param>
internal sealed record CatalogGraphModel(
    IReadOnlyList<CatalogGraphNode> Nodes,
    IReadOnlyDictionary<string, int> NodeIndexesById,
    IReadOnlyList<CatalogGraphEdge> Edges,
    IReadOnlyDictionary<int, CatalogGraphDetail> DetailsByNode,
    IReadOnlyList<CatalogClusterChip> Chips,
    IReadOnlyList<CatalogCyclicGroup> CyclicGroups,
    IReadOnlyDictionary<int, IReadOnlyList<int>> ChipIndexesByNode,
    CatalogInitialView InitialView,
    IReadOnlyList<CatalogSearchEntry> SearchIndex,
    int TableCount,
    int RelationCount,
    bool HasData,
    GraphViewOrigin Origin);
