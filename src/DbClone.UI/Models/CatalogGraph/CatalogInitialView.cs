namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// What the picture shows when it opens. Both narrowing decisions are product defaults, stated
/// apart from any readability claim: a first-screen size, and a density condition.
/// </summary>
/// <param name="ShowsWholeGraph">
/// Whether the whole database opens. False means a neighbourhood was chosen, or that no
/// neighbourhood was legible and the picture opens with no automatic focus at all.
/// </param>
/// <param name="FocusNodeIndex">The table the picture opens on, or <see langword="null"/> when none was chosen.</param>
/// <param name="VisibleNodeIndexes">
/// The nodes the picture opens on, in canonical order: every node when the whole database opens,
/// otherwise the focus with its one-step neighbours. Every other node is emitted and hidden, so
/// revealing it is a class toggle and never a second read of the catalog.
/// </param>
/// <param name="DenseCandidateCount">
/// How many related tables had a neighbourhood too large to open on. Non-zero with a null focus is
/// the dense case the header explains in the reader's own language.
/// </param>
/// <param name="TableCount">How many tables the catalog holds, for the explanation's own figures.</param>
internal sealed record CatalogInitialView(
    bool ShowsWholeGraph,
    int? FocusNodeIndex,
    IReadOnlyList<int> VisibleNodeIndexes,
    int DenseCandidateCount,
    int TableCount);
