namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// What the picture shows when it opens: every table the model holds, narrowed only by a table the
/// reader had already selected.
/// </summary>
/// <param name="ShowsWholeGraph">
/// Whether the whole database opens. False means the reader opened the view from a table they had
/// selected, so the picture opens on that table and its neighbours.
/// </param>
/// <param name="FocusNodeIndex">The table the reader selected, or <see langword="null"/> when none was chosen.</param>
/// <param name="VisibleNodeIndexes">
/// The nodes the picture opens on, in canonical order: every node when the whole database opens,
/// otherwise the focus with its one-step neighbours. Every other node is emitted and hidden, so
/// revealing it is a class toggle and never a second read of the catalog.
/// </param>
/// <param name="TableCount">How many tables the catalog holds, for the count line's own figures.</param>
internal sealed record CatalogInitialView(
    bool ShowsWholeGraph,
    int? FocusNodeIndex,
    IReadOnlyList<int> VisibleNodeIndexes,
    int TableCount);
