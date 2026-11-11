namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The computed picture: where every card sits and how every edge is drawn. Positions are absolute
/// in the emitted SVG's own coordinates, so the browser pans and zooms one group and computes no
/// layout of its own.
/// </summary>
/// <param name="Nodes">One placement per node, in node-index order.</param>
/// <param name="Edges">One route per edge, in edge-index order.</param>
/// <param name="Width">The drawing's full width, including margins.</param>
/// <param name="Height">The drawing's full height, including margins.</param>
internal sealed record CatalogGraphLayout(
    IReadOnlyList<NodePlacement> Nodes,
    IReadOnlyList<EdgeRoute> Edges,
    double Width,
    double Height);
