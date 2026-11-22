using System.IO;

using DbClone.Application.DTOs;
using DbClone.Application.Relationship;
using DbClone.UI.Services.GraphLayout;
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

        return ShowAsync(
            state,
            Working(FolderName(catalogPath), "Reading the stored catalog\u2026"),
            token => _graph.ReadCatalogAsync(catalogPath, token),
            cancellationToken);
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

        return ShowAsync(
            state,
            Working($"{connection.DatabaseName} ({Side(role)})", "Reading the database model\u2026"),
            token => _graph.ReadDatabaseAsync(connection, role, token),
            cancellationToken);
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
            var built = _builder.Build(view, cancellationToken);
            if (built.Document is not { } document)
            {
                // The catalog this picture describes is complete: a picture that could not be drawn is a
                // warning that names the reason, never a failed store (FR17).
                _logger.LogWarning("Relationship view could not be drawn for {CatalogPath}: {Reason}", catalogPath, built.Failure!.Message);
                state.LogWarning($"{OperationName} could not be written: {built.Failure!.Message}");
                return null;
            }

            if (built.IsDegraded)
            {
                WarnDegraded(state);
            }

            if (built.IsRough)
            {
                WarnPlain(state);
            }

            ReportArrangement(built);

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
    /// Opens the viewer, reads one origin, reports what it holds and hands the picture to whichever
    /// presenter the app resolved. The window comes first: the read is the long part, and a request that
    /// stays invisible until its picture exists is what the reader reported as nothing happening at all.
    /// A read that fails is an error stated in the window; a presentation that fails costs the window and
    /// nothing else.
    /// </summary>
    private async Task ShowAsync(
        WorkflowState state,
        RelationshipNotice opening,
        Func<CancellationToken, Task<RelationshipGraphView>> read,
        CancellationToken cancellationToken)
    {
        // The notice is the window's first page, so the origin is named and the work is stated before a
        // single fact has been read.
        var announced = await AnnounceAsync(opening, cancellationToken).ConfigureAwait(false);

        // A presenter with no window of its own — the browser — leaves only the pointer to say the
        // request was heard, so it is raised exactly when the notice could not be.
        if (!announced) _waitCursor.Show();

        try
        {
            var (view, reason) = await ReadAsync(read, state, cancellationToken).ConfigureAwait(false);
            if (view is null)
            {
                // Whatever stopped the read is stated in the window too, because a failure the reader
                // never sees is the defect this page exists for.
                if (reason is not null)
                {
                    await AnnounceAsync(Stopped(opening.Origin, "The origin could not be read", reason), cancellationToken)
                        .ConfigureAwait(false);
                }

                return;
            }

            await DrawAsync(view, Refresh(read, state), Redraw(), state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!announced) _waitCursor.Hide();
        }
    }

    /// <summary>
    /// One read, one picture: the search works out the arrangement the catalog is drawn with and the reader is
    /// handed it. Nothing is put on screen that the search would replace a moment later — a plain arrangement
    /// drawn first is a picture the reader reads, judges and saves before the answer exists, and a picture that
    /// arrives in two versions reads as two answers. The window states what is being read while the arrangement
    /// is worked out, exactly as it does while the origin is read.
    /// </summary>
    /// <param name="view">The graph to draw.</param>
    /// <param name="refresh">How the presenter re-reads the origin.</param>
    /// <param name="redraw">How the presenter draws the held origin for another selection.</param>
    /// <param name="state">The state the run reports into.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    private async Task DrawAsync(
        RelationshipGraphView view,
        Func<CancellationToken, Task<RelationshipDocument>> refresh,
        Func<IReadOnlySet<int>, RelationshipDocument> redraw,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        var built = await ArrangeAsync(view, timeLimit: null, state, cancellationToken).ConfigureAwait(false);
        if (built is null || built.Failure?.Kind == ELayoutFailureKind.Cancelled)
        {
            // A run stopped here is the caller's own: it is not announced as a picture that failed.
            return;
        }

        if (built.Document is not { } document)
        {
            // There is no picture at all to hand over, so the window states why instead of keeping the notice
            // it opened with.
            await StopAsync(view, built, state, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PresentBuiltAsync(view, document, built, refresh, redraw, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the one picture to the presenter, naming beside it what the search would have drawn differently: a
    /// picture that used the cheap path, or a plain one, is a warning under the picture rather than a reason to
    /// take the picture away.
    /// </summary>
    private async Task PresentBuiltAsync(
        RelationshipGraphView view,
        RelationshipDocument document,
        RelationshipDocumentResult built,
        Func<CancellationToken, Task<RelationshipDocument>> refresh,
        Func<IReadOnlySet<int>, RelationshipDocument> redraw,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        if (built.IsDegraded)
        {
            WarnDegraded(state);
        }

        if (built.IsRough)
        {
            WarnPlain(state);
        }

        ReportArrangement(built);

        await PresentAsync(view, document, refresh, redraw, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The last word when no picture could be arranged at all: the failure is written where the app records
    /// it and stated in the window that was opened for the request, never left as a progress notice.
    /// </summary>
    /// <param name="view">The graph that could not be drawn.</param>
    /// <param name="built">The failed arrangement.</param>
    /// <param name="state">The state that records the error.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    private async Task StopAsync(
        RelationshipGraphView view,
        RelationshipDocumentResult built,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        var reason = built.Failure?.Message ?? "no complete picture was reached";

        _logger.LogError("The relationship view of {Origin} could not be drawn: {Reason}", view.Origin.Label, reason);
        state.LastError = $"{OperationName} failed: {reason}";

        await AnnounceAsync(Stopped(view.Origin.Location, "The picture could not be drawn", reason), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Hands one document to the presenter. A presentation that fails is a warning, and the document the
    /// reader sees stays the one already on screen.
    /// </summary>
    /// <returns>Whether the document reached the presenter.</returns>
    private async Task<bool> PresentAsync(
        RelationshipGraphView view,
        RelationshipDocument document,
        Func<CancellationToken, Task<RelationshipDocument>> refresh,
        Func<IReadOnlySet<int>, RelationshipDocument> redraw,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        _presented = view;
        try
        {
            await _presenter
                .PresentAsync(document, refresh, redraw, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            state.Log($"{OperationName} cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The relationship view of {Origin} could not be shown", view.Origin.Label);
            state.LogWarning($"{OperationName} of {view.Origin.Label} could not be shown: {ex.Message}");

            return false;
        }
    }

    /// <summary>
    /// Arranges the picture off the thread that draws it, with the watchdog the caller names.
    /// <see langword="null"/> when the run was cancelled, which is not a picture and not a failure.
    /// </summary>
    /// <param name="view">The graph to draw.</param>
    /// <param name="timeLimit">The arrangement's watchdog; <see langword="null"/> for the search's own.</param>
    /// <param name="state">The state that records the cancellation.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The document, or <see langword="null"/> when the run was cancelled.</returns>
    private async Task<RelationshipDocumentResult?> ArrangeAsync(
        RelationshipGraphView view,
        TimeSpan? timeLimit,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task
                .Run(() => _builder.Build(view, NoChips, cancellationToken, timeLimit), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            state.Log($"{OperationName} cancelled.");
            return null;
        }
    }

    /// <summary>
    /// States a notice in the window, and answers whether the reader is looking at it. A presenter with
    /// no window says so, and a notice that cannot be stated is a warning rather than a failed request.
    /// </summary>
    private async Task<bool> AnnounceAsync(RelationshipNotice notice, CancellationToken cancellationToken)
    {
        try
        {
            return await _presenter.ShowNoticeAsync(notice, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The relationship view could not state what it was doing");
            return false;
        }
    }

    /// <summary>Reads the origin and reports it, or states the failure and yields the reason for it.</summary>
    private async Task<(RelationshipGraphView? View, string? Reason)> ReadAsync(
        Func<CancellationToken, Task<RelationshipGraphView>> read,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var view = await read(cancellationToken).ConfigureAwait(false);
            Report(view, state);

            return (view, null);
        }
        catch (OperationCanceledException)
        {
            state.Log($"{OperationName} cancelled.");
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} failed", OperationName);
            state.LastError = $"{OperationName} failed: {ex.Message}";

            return (null, ex.Message);
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

                // The presenter delegate carries a failure by throwing it, so the sentence the presenter
                // prints beside the document it could not replace is the arrangement's own.
                var built = _builder.Build(view, token);
                if (built.IsDegraded)
                {
                    WarnDegraded(state);
                }

                if (built.IsRough)
                {
                    WarnPlain(state);
                }

                ReportArrangement(built);

                return built.OrThrow();
            };

    /// <summary>
    /// The redraw a presenter offers: the same catalog arranged again for another cluster selection.
    /// Nothing is read a second time — a chip changes what the picture draws, never what the origin
    /// holds — and the tables that are not shown take no room in the arrangement (T28).
    /// </summary>
    private Func<IReadOnlySet<int>, RelationshipDocument> Redraw() =>
        offChips =>
        {
            var built = _builder.Build(
                _presented ?? throw new InvalidOperationException("No relationship view has been presented."),
                offChips);

            ReportArrangement(built);

            return built.OrThrow();
        };

    /// <summary>
    /// Writes down what the arrangement decided about the picture just built: the seed it chose, what its
    /// routes cost, the page it filled, the table most relations meet, and what every other seed did. The
    /// picture is judged by the numbers it was chosen by, and the seed a better picture needs is named by
    /// what stood in its way.
    /// </summary>
    /// <param name="built">The picture that was built, with the search's own account of it.</param>
    private void ReportArrangement(RelationshipDocumentResult built)
    {
        if (built.Facts is not { } facts)
        {
            return;
        }

        _logger.LogInformation(
            "Relationship picture: {Arrangement} arrangement, {Tables} tables / {Relations} relations, "
            + "{Crossings} crossings, {RankViolations} rank violations, {Width:0}x{Height:0}, "
            + "busiest {Busiest} ({BusiestRelations} ends){Flags}",
            facts.Arrangement,
            facts.Tables,
            facts.Relations,
            facts.Crossings,
            facts.RankViolations,
            facts.Width,
            facts.Height,
            facts.Busiest,
            facts.BusiestRelations,
            (built.IsDegraded ? ", degraded" : string.Empty) + (built.IsRough ? ", plain" : string.Empty));

        _logger.LogInformation("Relationship picture seeds: {Seeds}", facts.Seeds);
    }

    /// <summary>
    /// States that the picture is the cheapest complete arrangement the watchdog allowed rather than the one
    /// the search would have chosen: every table and every relation is there, the refining is not.
    /// </summary>
    private static void WarnDegraded(WorkflowState state) =>
        state.LogWarning($"{OperationName}: the arrangement ran out of time before it was refined; the picture is the best complete arrangement found.");

    /// <summary>
    /// States that the picture is the plain arrangement rather than one the search proved: every table and
    /// every relation is drawn, but a stroke may cross another stroke, and the reader is told so instead of
    /// being left to wonder. A stroke laid across a card is not part of the warning: the cards are painted
    /// over the ink, so such a stroke is hidden by the card rather than read as a line through it.
    /// </summary>
    private static void WarnPlain(WorkflowState state) =>
        state.LogWarning($"{OperationName}: the picture is the plain arrangement; some relations may cross one another.");

    /// <summary>The page the window opens with: the origin, what is being done to it, and when the picture comes.</summary>
    private static RelationshipNotice Working(string origin, string headline) =>
        new(origin, headline, "The picture opens as soon as it is arranged.");

    /// <summary>The page a stopped request leaves behind: the origin, what could not be done, and why.</summary>
    private static RelationshipNotice Stopped(string origin, string headline, string reason) =>
        new(origin, headline, reason, IsFailure: true);

    /// <summary>Which panel asked, in the words the window's title uses.</summary>
    private static string Side(ERelationshipRole role) => role switch
    {
        ERelationshipRole.Source => "source",
        ERelationshipRole.Target => "target",
        _ => "database",
    };

    /// <summary>The catalog folder's own name, which is what the reader called it.</summary>
    private static string FolderName(string catalogPath) =>
        Path.GetFileName(catalogPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>The selection that switches nothing off, which is how every first picture is drawn.</summary>
    private static readonly HashSet<int> NoChips = [];

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
