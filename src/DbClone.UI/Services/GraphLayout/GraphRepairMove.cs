namespace DbClone.UI.Services.GraphLayout;

/// <summary>One trial move of a card.</summary>
/// <param name="Node">The card to move.</param>
/// <param name="Dx">The horizontal shift.</param>
/// <param name="Dy">The vertical shift.</param>
/// <param name="Ordinal">The move's position in the deterministic enumeration; the last tie-breaker.</param>
internal sealed record GraphRepairMove(int Node, double Dx, double Dy, int Ordinal);
