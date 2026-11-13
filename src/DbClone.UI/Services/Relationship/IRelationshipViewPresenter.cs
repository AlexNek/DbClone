namespace DbClone.UI.Services.Relationship;

/// <summary>
/// How a built document becomes something a person can look at. One implementation hosts it in the app,
/// another hands it to the system's browser; the handler never learns which one it has.
/// </summary>
internal interface IRelationshipViewPresenter
{
    /// <summary>Shows one document, with a way to build it again on request.</summary>
    /// <param name="document">The document to show.</param>
    /// <param name="refresh">
    /// Re-reads the origin and rebuilds the document, or <see langword="null"/> when the presenter has no
    /// refresh affordance or none is available.
    /// </param>
    /// <param name="redraw">
    /// Draws the document again for another cluster selection, or <see langword="null"/> when the
    /// presenter cannot ask for one. A presenter that offers no control over the document ignores it.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    Task PresentAsync(
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>>? refresh,
        Func<IReadOnlySet<int>, RelationshipDocument>? redraw,
        CancellationToken ct);
}
