using DbClone.Application.DTOs;
using DbClone.Application.Relationship;
using DbClone.UI.Services.Relationship;
using DbClone.UI.StoreRestore.Services;
using DbClone.UI.ViewModels;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.StoreRestore.Handlers;

/// <summary>
/// The workflow behind the two "View relationships" affordances and behind the picture every finished
/// store leaves in its catalog (D12): read the origin, build the document, present it — and, for a
/// store, write it into the catalog.
/// </summary>
internal interface IRelationshipViewHandler
{
    /// <summary>Reads a stored catalog and presents its relationship document.</summary>
    /// <param name="catalogPath">The absolute path of a stored catalog folder.</param>
    /// <param name="state">The Store/Restore state the run reports into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ShowCatalogAsync(string catalogPath, WorkflowState state, CancellationToken cancellationToken = default);

    /// <summary>Reads a live database and presents its relationship document; nothing is written.</summary>
    /// <param name="connection">The side the button was pressed on — source or target.</param>
    /// <param name="role">Which side it is, as the log states it.</param>
    /// <param name="state">The Store/Restore state the run reports into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ShowDatabaseAsync(
        ConnectionInfo connection,
        ERelationshipRole role,
        WorkflowState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the picture into the catalog and presents nothing, which is what a finished store needs:
    /// the file is there before the reader asks for it (FR17).
    /// </summary>
    /// <param name="catalogPath">The absolute path of a stored catalog folder.</param>
    /// <param name="state">The Store/Restore state the run reports into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The picture's path, or <see langword="null"/> when nothing could be written.</returns>
    Task<string?> TryWriteAsync(string catalogPath, WorkflowState state, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read → build → present, ordered once. The view is a string in memory: viewing a catalog or a database
/// leaves no file behind (D1), and the one file the product still writes is the picture a store puts in
/// its catalog (D5, <see cref="RelationshipPictureWriter"/>).
/// </summary>
internal sealed class RelationshipViewHandler : IRelationshipViewHandler
{
    private const string OperationName = "Relationship view";

    private readonly IRelationshipGraphService _graph;
    private readonly RelationshipDocumentBuilder _builder;
    private readonly IRelationshipViewPresenter _presenter;
    private readonly RelationshipPictureWriter _pictureWriter;
    private readonly IWaitCursor _waitCursor;
    private readonly ILogger<RelationshipViewHandler> _logger;

    /// <summary>The catalog behind the document on screen, which a later redraw arranges again.</summary>
    private RelationshipGraphView? _presented;

    /// <summary>
    /// Composes the workflow from its collaborators. Public although the type is internal: the
    /// container only constructs a public constructor, and the services it receives are internal to
    /// this assembly.
    /// </summary>
    public RelationshipViewHandler(
        IRelationshipGraphService graph,
        RelationshipDocumentBuilder builder,
        IRelationshipViewPresenter presenter,
        RelationshipPictureWriter pictureWriter,
        IWaitCursor waitCursor,
        ILogger<RelationshipViewHandler> logger)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _pictureWriter = pictureWriter ?? throw new ArgumentNullException(nameof(pictureWriter));
        _waitCursor = waitCursor ?? throw new ArgumentNullException(nameof(waitCursor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task ShowCatalogAsync(
        string catalogPath,
        WorkflowState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(state);

        return ShowAsync(state, token => _graph.ReadCatalogAsync(catalogPath, token), cancellationToken);
    }

    /// <inheritdoc />
    public Task ShowDatabaseAsync(
        ConnectionInfo connection,
        ERelationshipRole role,
        WorkflowState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(state);

        return ShowAsync(state, token => _graph.ReadDatabaseAsync(connection, role, token), cancellationToken);
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
            var view = await _graph.ReadCatalogAsync(catalogPath, cancellationToken).ConfigureAwait(false);
            var document = _builder.Build(view);
            var picturePath = await _pictureWriter
                .WriteAsync(catalogPath, document.Html, cancellationToken)
                .ConfigureAwait(false);

            Report(view, state);
            state.LogDetail($"Picture: {picturePath}");

            return picturePath;
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
    /// Reads one origin, reports what it holds and hands the document to whichever presenter the app
    /// resolved. A read that fails is an error; a presentation that fails costs the reader the window and
    /// nothing else.
    /// </summary>
    private async Task ShowAsync(
        WorkflowState state,
        Func<CancellationToken, Task<RelationshipGraphView>> read,
        CancellationToken cancellationToken)
    {
        // Reading an origin and arranging its picture take long enough that the reader needs to be told
        // the request was heard: the waiting pointer covers the whole run, and the arrangement — which
        // holds nothing of the window's — is computed off the thread that draws it, so the pointer can
        // turn while the work goes on.
        _waitCursor.Show();
        try
        {
            var view = await ReadAsync(read, state, cancellationToken).ConfigureAwait(false);
            if (view is null) return;

            _presented = view;
            var document = await Task.Run(() => _builder.Build(view)).ConfigureAwait(false);

            try
            {
                await _presenter
                    .PresentAsync(document, Refresh(read, state), Redraw(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                state.Log($"{OperationName} cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The relationship view of {Origin} could not be shown", view.Origin.Label);
                state.LogWarning($"{OperationName} of {view.Origin.Label} could not be shown: {ex.Message}");
            }
        }
        finally
        {
            _waitCursor.Hide();
        }
    }

    /// <summary>Reads the origin and reports it, or states the failure and yields nothing to present.</summary>
    private async Task<RelationshipGraphView?> ReadAsync(
        Func<CancellationToken, Task<RelationshipGraphView>> read,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var view = await read(cancellationToken).ConfigureAwait(false);
            Report(view, state);

            return view;
        }
        catch (OperationCanceledException)
        {
            state.Log($"{OperationName} cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} failed", OperationName);
            state.LastError = $"{OperationName} failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The refresh a presenter offers: the same read and the same report, so a refreshed document says
    /// what the first one said. A failure travels to the presenter, which is the only party that can say
    /// so beside the document it could not replace.
    /// </summary>
    private Func<CancellationToken, Task<RelationshipDocument>> Refresh(
        Func<CancellationToken, Task<RelationshipGraphView>> read,
        WorkflowState state) =>
        async token =>
            {
                var view = await read(token).ConfigureAwait(false);
                Report(view, state);

                _presented = view;

                return _builder.Build(view);
            };

    /// <summary>
    /// The redraw a presenter offers: the same catalog arranged again for another cluster selection.
    /// Nothing is read a second time — a chip changes what the picture draws, never what the origin
    /// holds — and the tables that are not shown take no room in the arrangement (T28).
    /// </summary>
    private Func<IReadOnlySet<int>, RelationshipDocument> Redraw() =>
        offChips => _builder.Build(
            _presented ?? throw new InvalidOperationException("No relationship view has been presented."),
            offChips);

    /// <summary>States what was read, and warns when the origin holds no table to draw.</summary>
    private static void Report(RelationshipGraphView view, WorkflowState state)
    {
        state.Log($"{OperationName}: {view.Tables.Count} tables, {view.Edges.Count} relations");
        state.LogDetail($"Origin: {view.Origin.Label} ({view.Origin.Location})");

        if (view.Tables.Count == 0)
        {
            state.LogWarning($"{OperationName}: {view.Origin.Label} has no tables to draw.");
        }
    }
}
