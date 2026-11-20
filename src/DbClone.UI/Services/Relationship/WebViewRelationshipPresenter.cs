using DbClone.Application.Relationship;
using DbClone.UI.Threading;
using DbClone.UI.Views;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Shows a document in the app's own window (D6): one window per process, created on the UI thread the
/// first time it is asked for, holding the next document instead of opening a second one.
/// </summary>
/// <remarks>
/// A runtime that cannot start — absent, blocked, or a profile folder that cannot be written — is caught
/// here, logged, and handed to the browser for that one request, so the app never dies because a browser
/// engine did not start (D6, D11).
/// </remarks>
internal sealed class WebViewRelationshipPresenter : IInAppRelationshipPresenter
{
    private readonly BrowserRelationshipPresenter _fallback;
    private readonly IDialogService _dialogs;
    private readonly ILogger<WebViewRelationshipPresenter> _logger;

    private RelationshipViewWindow? _window;

    /// <summary>Creates the presenter over the fallback it hands a request to when the engine fails.</summary>
    /// <param name="fallback">The browser presenter, which also backs "open in browser" (D10).</param>
    /// <param name="dialogs">Asks the reader where to keep a copy for "save as".</param>
    /// <param name="logger">The app's logger.</param>
    public WebViewRelationshipPresenter(
        BrowserRelationshipPresenter fallback,
        IDialogService dialogs,
        ILogger<WebViewRelationshipPresenter> logger)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool HasWindow => _window is not null;

    /// <inheritdoc />
    /// <remarks>
    /// The window is created here even when nothing has been arranged yet, because the wait the reader
    /// reported is spent reading the origin: a viewer that opens first states the origin and the work,
    /// and the document replaces the notice in the same window.
    /// </remarks>
    public async Task<bool> ShowNoticeAsync(RelationshipNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);

        try
        {
            await UiDispatcher
                .InvokeAsync(() => NoticeOnUiThreadAsync(notice, ct))
                .ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // The runtime and not the request is what failed: the pointer takes the wait, and the
            // document still falls back to the browser when it is ready.
            _logger.LogWarning(ex, "The in-app relationship view could not be opened for its notice");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task PresentAsync(
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>>? refresh,
        Func<IReadOnlySet<int>, RelationshipDocument>? redraw,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        try
        {
            await UiDispatcher
                .InvokeAsync(() => PresentOnUiThreadAsync(document, refresh, redraw, ct))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The in-app relationship view could not be shown; the browser takes this one");
            await _fallback.PresentAsync(document, refresh, redraw, ct).ConfigureAwait(false);
        }
    }

    private async Task NoticeOnUiThreadAsync(RelationshipNotice notice, CancellationToken ct) =>
        await EnsureWindow().ShowNoticeAsync(notice, ct).ConfigureAwait(true);

    private async Task PresentOnUiThreadAsync(
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>>? refresh,
        Func<IReadOnlySet<int>, RelationshipDocument>? redraw,
        CancellationToken ct)
    {
        await EnsureWindow().ShowDocumentAsync(document, refresh, redraw, ct).ConfigureAwait(true);
    }

    /// <summary>The one window, created on the UI thread the first time either the notice or a document needs it.</summary>
    private RelationshipViewWindow EnsureWindow() =>
        _window ??= new RelationshipViewWindow(_logger, OpenInBrowserAsync, SaveAsync)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };

    /// <summary>The window's "open in browser": the one file rule, shared with the fallback (D10).</summary>
    private Task OpenInBrowserAsync(RelationshipDocument document, CancellationToken ct) =>
        _fallback.PresentAsync(document, refresh: null, redraw: null, ct);

    /// <summary>
    /// The window's "save as": a copy wherever the reader asks for it, of the page as it stands — a table
    /// they carried by hand is in the copy, because the arrangement they made is the one they asked to keep.
    /// </summary>
    private async Task SaveAsync(RelationshipDocument document, CancellationToken ct)
    {
        // The page is read before the reader is asked where to keep it: a page that cannot be read saves
        // nothing at all, rather than a file that quietly puts back every card they moved.
        var shown = _window is null ? null : await _window.ReadShownHtmlAsync().ConfigureAwait(true);

        var path = _dialogs.SaveFile("HTML File|*.html", SuggestedName(document.Origin));
        if (string.IsNullOrWhiteSpace(path)) return;

        await RelationshipViewFile.WriteToAsync(path, shown ?? document.Html, ct).ConfigureAwait(true);
    }

    /// <summary>A name a reader can keep: the origin's own label, with nothing a file system refuses.</summary>
    private static string SuggestedName(GraphViewOrigin origin)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string([.. origin.Label.Select(character => invalid.Contains(character) ? '_' : character)]);

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{safe}-relationships.html");
    }
}
