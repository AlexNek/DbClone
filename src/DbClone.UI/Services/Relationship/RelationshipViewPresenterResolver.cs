namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Chooses how one document is shown (D4): the in-app window when a window is already open or a runtime
/// answers the probe, the browser otherwise.
/// </summary>
/// <remarks>
/// The probe runs on every request that has no window yet, so a runtime installed while the app is
/// running is picked up by the next view without a restart. Once a window exists it keeps the document:
/// a runtime that disappears later cannot take away a window the reader already has.
/// </remarks>
internal sealed class RelationshipViewPresenterResolver : IRelationshipViewPresenter
{
    private readonly IWebViewRuntimeProbe _probe;
    private readonly IInAppRelationshipPresenter _window;
    private readonly IRelationshipViewPresenter _browser;

    /// <summary>Creates the resolver over the probe and the two presenters it chooses between.</summary>
    /// <param name="probe">Answers whether the in-app runtime is installed.</param>
    /// <param name="window">The in-app presenter.</param>
    /// <param name="browser">The fallback presenter.</param>
    public RelationshipViewPresenterResolver(
        IWebViewRuntimeProbe probe,
        IInAppRelationshipPresenter window,
        IRelationshipViewPresenter browser)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    }

    /// <inheritdoc />
    public Task<bool> ShowNoticeAsync(RelationshipNotice notice, CancellationToken ct) =>
        Choose().ShowNoticeAsync(notice, ct);

    /// <inheritdoc />
    public Task PresentAsync(
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>>? refresh,
        Func<IReadOnlySet<int>, RelationshipDocument>? redraw,
        CancellationToken ct) =>
        Choose().PresentAsync(document, refresh, redraw, ct);

    private IRelationshipViewPresenter Choose() =>
        _window.HasWindow || _probe.IsAvailable() ? _window : _browser;
}
