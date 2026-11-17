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
/// <param name="InRings">
/// Whether the cards were arranged in rings around one middle table rather than in bands. The picture's
/// writers read nothing from it; the document does — an arrow of a ringed picture is a chord across
/// empty space, so a card the reader carries by hand has its arrows drawn again the same way, with the
/// ends of every card dealt along the borders they face.
/// </param>
internal sealed record CatalogGraphLayout(
    IReadOnlyList<NodePlacement> Nodes,
    IReadOnlyList<EdgeRoute> Edges,
    double Width,
    double Height,
    bool InRings);
