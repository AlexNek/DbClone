namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Shows a document by handing it to whatever the user's system uses for HTML (D10): the fallback when
/// no in-app runtime is available, so the feature works on a machine that has installed nothing extra.
/// </summary>
/// <remarks>
/// The document is written to the one fixed path before the shell is asked, so a browser that refuses
/// the file still leaves the reader something to open by hand.
/// </remarks>
internal sealed class BrowserRelationshipPresenter : IRelationshipViewPresenter
{
    private readonly IHtmlFileOpener _opener;

    /// <summary>Creates the presenter over the shell opener.</summary>
    /// <param name="opener">Hands the written file to the system.</param>
    public BrowserRelationshipPresenter(IHtmlFileOpener opener) =>
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));

    /// <inheritdoc />
    /// <remarks>
    /// A browser has no page this app can write a progress notice into before the document exists, and
    /// a second file on disk for one would be a file the product does not keep (D1). The pointer covers
    /// the wait until the document can be handed over.
    /// </remarks>
    public Task<bool> ShowNoticeAsync(RelationshipNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="refresh"/> and <paramref name="redraw"/> are unused: a browser tab has no
    /// control this app can offer, and a reader who wants a fresh document asks for a new view.
    /// </remarks>
    public async Task PresentAsync(
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>>? refresh,
        Func<IReadOnlySet<int>, RelationshipDocument>? redraw,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        await RelationshipViewFile.WriteAsync(document.Html, ct).ConfigureAwait(false);
        _opener.Open(RelationshipViewFile.Path);
    }
}
