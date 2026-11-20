namespace DbClone.UI.Services.GraphLayout;

/// <summary>A candidate arrangement of cards and nothing else; it is not accepted until every relation is routed and validated.</summary>
/// <param name="Name">Which seed produced the draft.</param>
/// <param name="Nodes">One placement per node, in node-index order.</param>
/// <param name="Width">The canvas width, margins included.</param>
/// <param name="Height">The canvas height, margins included.</param>
internal sealed record GraphDraftLayout(string Name, IReadOnlyList<NodePlacement> Nodes, double Width, double Height);
