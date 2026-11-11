using System.IO;
using System.Text;

using DbClone.Application.Interchange;
using DbClone.UI.Services;
using DbClone.UI.Services.GraphLayout;
using DbClone.UI.StoreRestore.Services;
using DbClone.UI.ViewModels;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.StoreRestore.Handlers;

/// <summary>
/// Renders the relationship view of one stored catalog and opens it (D6). The path rule, the three
/// stages and the opener are all collaborators, so this type only orders them.
/// </summary>
internal interface IRelationshipViewHandler
{
    /// <summary>Reads a catalog, writes its picture into it and hands the file to the shell.</summary>
    /// <param name="catalogPath">The absolute path of a stored catalog folder.</param>
    /// <param name="state">The Store/Restore state the run reports into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ShowAsync(string catalogPath, WorkflowState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the picture into the catalog and opens nothing, which is what a finished store needs:
    /// the file is there before the reader asks for it (FR17).
    /// </summary>
    /// <param name="catalogPath">The absolute path of a stored catalog folder.</param>
    /// <param name="state">The Store/Restore state the run reports into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The picture's path, or <see langword="null"/> when nothing could be written.</returns>
    Task<string?> TryWriteAsync(string catalogPath, WorkflowState state, CancellationToken cancellationToken = default);
}

/// <summary>
/// The workflow behind the "View relationships" affordances and behind the picture every finished
/// store leaves in its catalog: read → analyze → lay out → emit → write into the catalog, and, when it
/// was asked for, open. The picture is the only file the app adds to a catalog (D5), and a write that
/// cannot land leaves neither a partial document nor a temporary file (T8).
/// </summary>
internal sealed class RelationshipViewHandler : IRelationshipViewHandler
{
    private const string OperationName = "Relationship view";

    /// <summary>The suffix the document is assembled under before it is moved into place.</summary>
    private const string TemporarySuffix = ".tmp";

    /// <summary>UTF-8 without a byte-order mark, the encoding the product's own writers use.</summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IInterchangeService _interchange;
    private readonly CatalogGraphAnalyzer _analyzer;
    private readonly GraphLayoutEngine _layout;
    private readonly CatalogGraphHtmlRenderer _renderer;
    private readonly IHtmlFileOpener _opener;
    private readonly ILogger<RelationshipViewHandler> _logger;

    /// <summary>
    /// Composes the workflow from its collaborators. Public although the type is internal: the
    /// container only constructs a public constructor, and the three stage services it receives are
    /// internal to this assembly.
    /// </summary>
    public RelationshipViewHandler(
        IInterchangeService interchange,
        CatalogGraphAnalyzer analyzer,
        GraphLayoutEngine layout,
        CatalogGraphHtmlRenderer renderer,
        IHtmlFileOpener opener,
        ILogger<RelationshipViewHandler> logger)
    {
        _interchange = interchange ?? throw new ArgumentNullException(nameof(interchange));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ShowAsync(
        string catalogPath,
        WorkflowState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            var picturePath = await WriteAsync(catalogPath, state, cancellationToken).ConfigureAwait(false);
            TryOpen(picturePath, state);
        }
        catch (OperationCanceledException)
        {
            state.Log($"{OperationName} cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relationship view failed for {CatalogPath}", catalogPath);
            state.LastError = $"{OperationName} failed: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public async Task<string?> TryWriteAsync(
        string catalogPath,
        WorkflowState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            return await WriteAsync(catalogPath, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // The catalog this picture describes is complete: a picture that could not be written is a
            // warning that names the reason, never a failed store (FR17).
            _logger.LogError(ex, "Relationship view could not be written for {CatalogPath}", catalogPath);
            state.LogWarning($"{OperationName} could not be written: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Renders the document and moves it into place inside the catalog. Throws when nothing can be
    /// written, so each caller decides what a failure means for the run it reports into.
    /// </summary>
    private async Task<string> WriteAsync(
        string catalogPath,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        // D5: inside the catalog folder, named after it — derived, never configured.
        var picturePath = RelationshipPicturePath.For(catalogPath);

        var view = await _interchange.ReadGraphAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        var model = _analyzer.Analyze(view);
        var layout = _layout.Compute(model);
        var document = _renderer.Render(view, model, layout, DateTimeOffset.Now);

        await WriteAtomicallyAsync(picturePath, document, cancellationToken).ConfigureAwait(false);

        state.Log($"Relationship view: {view.Tables.Count} tables, {view.Edges.Count} relations");
        state.LogDetail($"Picture: {picturePath}");

        return picturePath;
    }

    /// <summary>
    /// Opens the file that is already complete on disk. A failure here costs the reader the shortcut
    /// and nothing else, so it is a warning that names the file instead of an error that blames the run.
    /// </summary>
    private void TryOpen(string picturePath, WorkflowState state)
    {
        try
        {
            _opener.Open(picturePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The relationship view could not be opened: {PicturePath}", picturePath);
            state.LogWarning($"{OperationName} was written to {picturePath} but could not be opened: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the document beside the target and moves it into place, so a reader never finds half a
    /// document and a failed write leaves the previous file as it was.
    /// </summary>
    private static async Task WriteAtomicallyAsync(
        string path,
        string document,
        CancellationToken cancellationToken)
    {
        var temporary = path + TemporarySuffix;

        try
        {
            await File.WriteAllTextAsync(temporary, document, NoBomUtf8, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Best-effort removal of the temporary file; never masks the failure that is on its way out.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A temporary file that cannot be removed is not worth a second failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same here: the original write failure is the one the log has to carry.
        }
    }
}
