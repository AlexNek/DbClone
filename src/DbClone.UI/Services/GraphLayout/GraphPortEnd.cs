namespace DbClone.UI.Services.GraphLayout;

/// <summary>One end of a relation as the port assigner sees it.</summary>
/// <param name="Edge">The relation's index.</param>
/// <param name="Node">The card this end sits on.</param>
/// <param name="Peer">The card at the other end; the same card for a self-reference.</param>
/// <param name="IsChild">Whether this is the child end.</param>
/// <param name="Ordinal">The relation's position among the relations joining the same pair.</param>
internal sealed record GraphPortEnd(int Edge, int Node, int Peer, bool IsChild, int Ordinal);
