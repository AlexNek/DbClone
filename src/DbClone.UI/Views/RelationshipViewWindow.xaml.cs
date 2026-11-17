using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;

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
/// <para>
/// The document is answered through <see cref="CoreWebView2.WebResourceRequested"/> rather than
/// <c>NavigateToString</c>, which caps a document at 2 MB while the data island carries every column,
/// key, index and check expression (D5). Every other address is refused, so the document cannot reach
/// the network even if a future edit adds an external reference.
/// </para>
/// <para>
/// The document may speak once, and only about one thing: which clusters the reader switched off, so
/// the window can draw the picture again without the tables that are not shown and the cards that
/// remain are arranged for them (T28). The message never carries a fact from the catalog.
/// </para>
/// <para>
/// The window reads one value back out of the document and writes none: where its picture stands, so a
/// document served again for another cluster selection opens where the reader left the one it replaces
/// — a chip changes what is drawn, never where the reader is looking (T28).
/// </para>
/// </remarks>
internal sealed partial class RelationshipViewWindow : FluentWindow
{
    /// <summary>A reserved top-level domain: it can never resolve to a real host.</summary>
    private const string Host = "https://dbclone.invalid";

    /// <summary>The one address the viewer answers; the query carries a version, never a document.</summary>
    private const string DocumentAddress = Host + "/graph.html";

    /// <summary>UTF-8 without a byte-order mark, the encoding every writer in the product uses.</summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The selection that switches nothing off.</summary>
    private static readonly HashSet<int> EmptyChips = [];

    /// <summary>How many chips one document may state, which no catalog's bar exceeds.</summary>
    private const int MaxOffChips = 512;

    /// <summary>
    /// How long the one message may be: a selection of <see cref="MaxOffChips"/> indexes never needs more,
    /// so a larger payload is not a selection and is not read.
    /// </summary>
    private const int MaxMessageLength = 8 * 1024;

    /// <summary>
    /// The one expression the viewer evaluates in the document: where its picture stands. Reading it
    /// before the document is served again is what keeps a chip from moving the view (D6, T28).
    /// </summary>
    private const string CameraExpression = "window.dbcloneCamera ? window.dbcloneCamera() : null";

    private readonly ILogger _logger;
    private readonly Func<RelationshipDocument, CancellationToken, Task> _openInBrowser;
    private readonly Func<RelationshipDocument, CancellationToken, Task> _saveAs;

    private RelationshipDocument? _document;
    private Func<CancellationToken, Task<RelationshipDocument>>? _refresh;
    private Func<IReadOnlySet<int>, RelationshipDocument>? _reshape;
    private IReadOnlySet<int> _offChips = EmptyChips;
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
    /// <param name="reshape">
    /// How to draw the same origin for another cluster selection, or <see langword="null"/> when the
    /// document cannot be drawn again.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal async Task ShowDocumentAsync(
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>>? refresh,
        Func<IReadOnlySet<int>, RelationshipDocument>? reshape,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!IsVisible) Show();
        Activate();

        // A new view of an origin starts at the whole catalog, whatever the previous one was showing,
        // and at the picture's own place, because the previous camera is no longer meaningful here.
        _offChips = EmptyChips;

        await SetDocumentAsync(document, refresh, reshape, camera: null, ct).ConfigureAwait(true);
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
        Func<IReadOnlySet<int>, RelationshipDocument>? reshape,
        GraphCamera? camera,
        CancellationToken ct)
    {
        _document = document;
        _refresh = refresh;
        _reshape = reshape;

        RefreshButton.IsEnabled = refresh is not null;
        Status.Text = string.Empty;

        var title = $"Relationship view \u2014 {Describe(document.Origin)}";
        Title = title;
        Bar.Title = title;

        await EnsureRuntimeAsync(ct).ConfigureAwait(true);

        Navigate(camera);
    }

    /// <summary>
    /// Takes the viewer to the document it now holds: a new address every time, so nothing cached can
    /// answer with the previous picture, and the reader's camera in the query when the same origin is
    /// served again, so the picture opens where they left it instead of at its own size.
    /// </summary>
    /// <param name="camera">Where the picture stands, or <see langword="null"/> for its own place.</param>
    private void Navigate(GraphCamera? camera)
    {
        _version++;
        _core!.Navigate($"{DocumentAddress}?v={_version}{camera?.Query() ?? string.Empty}");
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
        _core.Settings.IsWebMessageEnabled = true;
        _core.Settings.AreDefaultScriptDialogsEnabled = false;

        _core.AddWebResourceRequestedFilter(DocumentAddress + "*", CoreWebView2WebResourceContext.All);
        _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += OnWebResourceRequested;
        _core.WebMessageReceived += OnWebMessage;
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

    /// <summary>
    /// The one message a document may post: the clusters the reader switched off. A payload of any other
    /// shape is ignored rather than guessed at, and a document that posts nothing changes nothing.
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_core is null || _reshape is null) return;
        if (!TryReadOffChips(e.WebMessageAsJson, out var offChips)) return;

        _ = ReshapeAsync(offChips);
    }

    /// <summary>
    /// Draws the held document again for the clusters that are now on. Nothing is read: the origin is
    /// the one the document already describes, and only the arrangement changes. The camera is read
    /// from the picture on screen first, so the picture that replaces it stands where it did.
    /// </summary>
    private async Task ReshapeAsync(IReadOnlySet<int> offChips)
    {
        var reshape = _reshape;
        if (reshape is null || _core is null) return;

        // Choosing a cluster arranges the picture again, which is the slowest thing a click here can ask
        // for. The arrangement holds nothing of the window's, so it is worked out on another thread while
        // the window wears its waiting pointer — and the pointer is what tells the reader that the click
        // was heard and the picture on screen is still the one being replaced.
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var document = await Task.Run(() => reshape(offChips)).ConfigureAwait(true);
            _document = document;
            _offChips = offChips;

            var camera = await ReadCameraAsync().ConfigureAwait(true);

            // Another selection replaced this one while the camera was being read: the address
            // belongs to the picture it was built for, and that picture is already on its way.
            if (!ReferenceEquals(_document, document)) return;

            Navigate(camera);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The relationship view could not be drawn again for another selection");
            Status.Text = ex.Message;
        }
        finally
        {
            Cursor = null;
        }
    }

    /// <summary>
    /// Where the picture on screen stands, as the document itself answers. A camera that cannot be
    /// read is no camera: the document opens at its own size rather than at a guess.
    /// </summary>
    private async Task<GraphCamera?> ReadCameraAsync()
    {
        if (_core is null) return null;

        try
        {
            var statement = await _core.ExecuteScriptAsync(CameraExpression).ConfigureAwait(true);

            return GraphCamera.TryRead(statement, out var camera) ? camera : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The relationship view could not read where its picture stood");
            return null;
        }
    }

    /// <summary>
    /// Reads the one message a document may post: the clusters the reader switched off, which the script
    /// sends as a JSON string holding <c>{"off":[index,…]}</c>. A payload that is not exactly that
    /// is refused whole, so a document can state a selection but never choose what is read (D5).
    /// </summary>
    /// <param name="json">The message as the runtime states it.</param>
    /// <param name="offChips">The chips the reader switched off, by chip index.</param>
    /// <returns>Whether the message was a cluster selection.</returns>
    internal static bool TryReadOffChips(string? json, out IReadOnlySet<int> offChips)
    {
        offChips = EmptyChips;

        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxMessageLength) return false;

        try
        {
            using var message = JsonDocument.Parse(json);
            var payload = message.RootElement;

            // The script posts a string, and the runtime states that string as a JSON string.
            if (payload.ValueKind != JsonValueKind.String)
            {
                return TryReadSelection(payload, out offChips);
            }

            using var selection = JsonDocument.Parse(payload.GetString() ?? string.Empty);
            return TryReadSelection(selection.RootElement, out offChips);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadSelection(JsonElement payload, out IReadOnlySet<int> offChips)
    {
        offChips = EmptyChips;

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("off", out var off)
            || off.ValueKind != JsonValueKind.Array
            || off.GetArrayLength() > MaxOffChips)
        {
            return false;
        }

        var chips = new HashSet<int>();
        foreach (var entry in off.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt32(out var index) || index < 0)
            {
                return false;
            }

            chips.Add(index);
        }

        offChips = chips;
        return true;
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
            var camera = await ReadCameraAsync().ConfigureAwait(true);
            var document = await _refresh(CancellationToken.None).ConfigureAwait(true);

            // A refresh re-reads the origin, not the reader: the clusters that were on stay on and the
            // picture keeps the place it stood, so a re-read changes what it says, not where it is.
            if (_reshape is not null && _offChips.Count > 0)
            {
                document = _reshape(_offChips);
            }

            await SetDocumentAsync(document, _refresh, _reshape, camera, CancellationToken.None).ConfigureAwait(true);
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
