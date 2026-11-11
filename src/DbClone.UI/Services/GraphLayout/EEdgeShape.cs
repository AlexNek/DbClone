namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The three shapes an edge can take. Each stands for exactly one fact and none of them substitutes
/// for another: a loop is a table pointing at itself, a detour is a constraint that closes a cycle,
/// and a dash — which is a style, not a shape — means a table outside the catalog.
/// </summary>
internal enum EEdgeShape
{
    /// <summary>
    /// A constraint between two different parts of the picture, drawn straight where the way is clear
    /// and as a dog-leg through a corridor where a card is in the way.
    /// </summary>
    Line,

    /// <summary>A constraint whose two ends sit in the same cyclic group, so it closes a cycle.</summary>
    Detour,

    /// <summary>A table pointing at itself.</summary>
    Loop,
}
