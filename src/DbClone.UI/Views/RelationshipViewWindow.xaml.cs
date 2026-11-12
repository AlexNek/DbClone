using System.ComponentModel;
using System.IO;
using System.Text;

using DbClone.Application.Relationship;
using DbClone.UI.Services.Relationship;

using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

using Wpf.Ui.Controls;

namespace DbClone.UI.Views;

/// <summary>
/// The in-app viewer (D6): one reusable window that hosts the document served from memory, with the
/// three explicit actions a reader needs — re-read the origin, hand the document to the browser, keep a
/// copy. It never learns where the document came from beyond the origin it was handed.
/// </summary>
/// <remarks>
/// The document is answered through <see cref="CoreWebView2.WebResourceRequested"/> rather than
/// <c>NavigateToString</c>, which caps a document at 2 MB while the data island carries every column,
/// key, index and check expression (D5). Every other address is refused, so the document cannot reach
/// the network even if a future edit adds an external reference.
/// </remarks>
internal sealed partial class RelationshipViewWindow : FluentWindow
{
    /// <summary>A reserved top-level domain: it can never resolve to a real host.</summary>
    private const string Host = "https://dbclone.invalid";

    /// <summary>The one address the viewer answers; the query carries a version, never a document.</summary>
    private const string DocumentAddress = Host + "/graph.html";

    /// <summary>UTF-8 without a byte-order mark, the encoding every writer in the product uses.</summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger _logger;
    private readonly Func<RelationshipDocument, CancellationToken, Task> _openInBrowser;
    private readonly Func<RelationshipDocument, CancellationToken, Task> _saveAs;

    private RelationshipDocument? _document;
    private Func<CancellationToken, Task<RelationshipDocument>>? _refresh;
    private CoreWebView2? _core;
    private int _version;

    /// <summary>Creates the window over the two actions the reader can take on the document it holds.</summary>
    /// <param name="logger">The app's logger.</param>
    /// <param name="openInBrowser">Writes the document to the fallback path and hands it to the shell.</param>
    /// <param name="saveAs">Asks where to keep a copy and writes it there.</param>
    public RelationshipViewWindow(
        ILogger logger,
        Func<RelationshipDocument, CancellationToken, Task> openInBrowser,
        Func<RelationshipDocument, CancellationToken, Task> saveAs)
    {
        InitializeComponent();

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _openInBrowser = openInBrowser ?? throw new ArgumentNullException(nameof(openInBrowser));
        _saveAs = saveAs ?? throw new ArgumentNullException(nameof(saveAs));
    }

    /// <summary>Shows one document, replacing whatever the window held, and reuses the window itself.</summary>
    /// <param name="document">The document to show.</param>
    /// <param name="refresh">How to build the next one, or <see langword="null"/> when it cannot be built.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal async Task ShowDocumentAsync(
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>>? refresh,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!IsVisible) Show();
        Activate();

        await SetDocumentAsync(document, refresh, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The window is kept for the next view: a close request hides it, and the process still ends when
    /// the main window closes, because the app shuts down on that event (D6).
    /// </summary>
    /// <param name="e">The closing arguments; the close is cancelled.</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();

        base.OnClosing(e);
    }

    private async Task SetDocumentAsync(
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>>? refresh,
        CancellationToken ct)
    {
        _document = document;
        _refresh = refresh;

        RefreshButton.IsEnabled = refresh is not null;
        Status.Text = string.Empty;

        var title = $"Relationship view \u2014 {Describe(document.Origin)}";
        Title = title;
        Bar.Title = title;

        await EnsureRuntimeAsync(ct).ConfigureAwait(true);

        // Every document is a new address, so nothing cached can answer with the previous picture.
        _version++;
        _core!.Navigate($"{DocumentAddress}?v={_version}");
    }

    /// <summary>Starts the engine once, in one profile folder per install, and blocks everything it may fetch.</summary>
    private async Task EnsureRuntimeAsync(CancellationToken ct)
    {
        if (_core is not null) return;

        ct.ThrowIfCancellationRequested();

        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolder.Name,
            "WebView2");
        Directory.CreateDirectory(profile);

        var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: profile,
                options: new CoreWebView2EnvironmentOptions(
                    additionalBrowserArguments: "--disk-cache-size=4194304 --inprivate"))
            .ConfigureAwait(true);

        await View.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        _core = View.CoreWebView2;
        _core.Settings.AreDevToolsEnabled = false;
        _core.Settings.AreDefaultContextMenusEnabled = false;
        _core.Settings.IsStatusBarEnabled = false;
        _core.Settings.IsWebMessageEnabled = false;
        _core.Settings.AreDefaultScriptDialogsEnabled = false;

        _core.AddWebResourceRequestedFilter(DocumentAddress + "*", CoreWebView2WebResourceContext.All);
        _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += OnWebResourceRequested;
    }

    /// <summary>Answers the document's own address from memory and refuses every other address.</summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_core is null) return;

        if (e.Request.Uri.StartsWith(DocumentAddress, StringComparison.OrdinalIgnoreCase))
        {
            var body = new MemoryStream(NoBomUtf8.GetBytes(_document?.Html ?? string.Empty));
            e.Response = _core.Environment.CreateWebResourceResponse(
                body,
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");

            return;
        }

        e.Response = _core.Environment.CreateWebResourceResponse(
            null,
            403,
            "Forbidden",
            "Content-Length: 0\r\n");
    }

    private void OnRefreshClick(object sender, System.Windows.RoutedEventArgs e) => _ = RefreshAsync();

    /// <summary>Re-runs the delegate the handler supplied: the same read, the same words, a new document.</summary>
    private async Task RefreshAsync()
    {
        if (_refresh is null) return;

        RefreshButton.IsEnabled = false;
        Status.Text = "Reading the origin again\u2026";

        try
        {
            var document = await _refresh(CancellationToken.None).ConfigureAwait(true);
            await SetDocumentAsync(document, _refresh, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The relationship view could not be refreshed");

            // The previous document stays on screen, and the toolbar says why it did not change.
            Status.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnOpenInBrowserClick(object sender, System.Windows.RoutedEventArgs e) =>
        _ = RunAsync(document => _openInBrowser(document, CancellationToken.None));

    private void OnSaveClick(object sender, System.Windows.RoutedEventArgs e) =>
        _ = RunAsync(document => _saveAs(document, CancellationToken.None));

    /// <summary>Runs one toolbar action against the document on screen, and says so when it fails.</summary>
    private async Task RunAsync(Func<RelationshipDocument, Task> action)
    {
        if (_document is null) return;

        try
        {
            await action(_document).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A relationship view action failed");
            Status.Text = ex.Message;
        }
    }

    /// <summary>The origin as the title states it: a live database by its address, a catalog by its folder.</summary>
    private static string Describe(GraphViewOrigin origin) =>
        origin.Kind == EGraphOriginKind.LiveDatabase
            ? $"live \u00b7 {origin.Location}"
            : $"catalog \u00b7 {origin.Label}";
}
