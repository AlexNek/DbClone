using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Builds the document from a graph: analyze, lay out, render — the three stages, ordered once here so
/// every caller produces the same document.
/// </summary>
/// <remarks>
/// The document is a string and nothing else: it names no path, so a live read and a stored catalog are
/// rendered by one pipeline and neither leaves a trace on disk.
/// </remarks>
internal sealed class RelationshipDocumentBuilder
{
    private readonly CatalogGraphAnalyzer _analyzer;
    private readonly GraphLayoutEngine _layout;
    private readonly CatalogGraphHtmlRenderer _renderer;

    /// <summary>Creates the builder over the three stages.</summary>
    /// <param name="analyzer">Turns the graph into the model the picture is drawn from.</param>
    /// <param name="layout">Places the cards and routes the arrows.</param>
    /// <param name="renderer">Emits the document.</param>
    public RelationshipDocumentBuilder(
        CatalogGraphAnalyzer analyzer,
        GraphLayoutEngine layout,
        CatalogGraphHtmlRenderer renderer)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    /// <summary>Renders one graph into the document that describes it.</summary>
    /// <param name="view">The graph, read from a catalog or from a live database.</param>
    /// <returns>The document, or the layout failure that stopped it.</returns>
    internal RelationshipDocumentResult Build(RelationshipGraphView view) => Build(view, EmptyChips, default);

    /// <summary>Renders one graph, the caller's cancellation reaching the arrangement.</summary>
    /// <param name="view">The graph, read from a catalog or from a live database.</param>
    /// <param name="cancellation">The caller's own cancellation.</param>
    /// <returns>The document, or the layout failure that stopped it.</returns>
    internal RelationshipDocumentResult Build(RelationshipGraphView view, CancellationToken cancellation) =>
        Build(view, EmptyChips, cancellation);

    /// <summary>
    /// Renders one graph with the clusters the reader switched off left out of the picture, so the
    /// cards that remain are arranged for the picture the reader is actually looking at instead of
    /// standing in a lattice sized for tables that are not drawn.
    /// </summary>
    /// <param name="view">The graph, read from a catalog or from a live database.</param>
    /// <param name="offChips">The chips the reader switched off, by chip index.</param>
    /// <returns>The document, or the layout failure that stopped it.</returns>
    internal RelationshipDocumentResult Build(RelationshipGraphView view, IReadOnlySet<int> offChips) =>
        Build(view, offChips, default);

    /// <summary>The three stages, for one picture drawn with a selection and a token.</summary>
    /// <param name="view">The graph, read from a catalog or from a live database.</param>
    /// <param name="offChips">The chips the reader switched off, by chip index.</param>
    /// <param name="cancellation">The caller's own cancellation.</param>
    /// <param name="timeLimit">
    /// The arrangement's watchdog, <see cref="GraphSearchBudget.MaximumElapsedLayoutTime"/> when omitted. A
    /// limit of zero answers with the plain complete arrangement, which is how the first picture reaches the
    /// reader while the refining is still to come.
    /// </param>
    /// <returns>The document, or the layout failure that stopped it.</returns>
    internal RelationshipDocumentResult Build(
        RelationshipGraphView view,
        IReadOnlySet<int> offChips,
        CancellationToken cancellation,
        TimeSpan? timeLimit = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(offChips);

        // The whole catalog is analyzed first: the chip bar, the count line and the panel keep stating
        // it, and only the cards are laid out for the clusters that are on.
        var whole = _analyzer.Analyze(view);
        var hidden = Hidden(whole, offChips);
        var shown = hidden.Count == 0 ? view : view.Without(hidden);
        var model = hidden.Count == 0 ? whole : _analyzer.Analyze(shown);

        // A picture the arrangement could not finish is reported instead of a document that would be
        // missing relations: the caller says what happened beside the picture it could not replace.
        var result = _layout.Compute(model, cancellation, timeLimit);
        if (result.Layout is not { } layout)
        {
            return RelationshipDocumentResult.Failed(result.Failure!);
        }

        return RelationshipDocumentResult.Success(
            new RelationshipDocument(
                view.Origin,
                _renderer.Render(shown, model, layout, DateTimeOffset.Now, whole, offChips)),
            result.IsDegraded,
            result.IsRough,
            result.Facts);
    }

    /// <summary>The selection that switches nothing off, which is how every document starts.</summary>
    private static readonly HashSet<int> EmptyChips = [];

    /// <summary>
    /// The tables a chip selection hides: a table goes when every chip of one of its axes is off,
    /// which is the rule the document's own script applies to a saved copy — resolved here instead,
    /// so the picture can be laid out without the tables that are not drawn.
    /// </summary>
    /// <param name="model">The analyzed whole catalog, with its chips and their claim sets.</param>
    /// <param name="offChips">The chips the reader switched off, by chip index.</param>
    /// <returns>The ids of the captured tables the selection hides.</returns>
    private static HashSet<string> Hidden(CatalogGraphModel model, IReadOnlySet<int> offChips)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (offChips.Count == 0)
        {
            return hidden;
        }

        var kinds = new Dictionary<int, EClusterChipKind>(model.Chips.Count);
        foreach (var chip in model.Chips)
        {
            kinds[chip.Index] = chip.Kind;
        }

        foreach (var node in model.Nodes)
        {
            if (!node.IsCaptured
                || !model.ChipIndexesByNode.TryGetValue(node.Index, out var claims)
                || claims.Count == 0)
            {
                continue;
            }

            // Each axis filters on its own: a schema the reader switched off hides its tables even
            // when a cyclic-group chip also claims them (T27).
            var axes = new Dictionary<EClusterChipKind, (int All, int Off)>();
            foreach (var claim in claims)
            {
                if (!kinds.TryGetValue(claim, out var kind))
                {
                    continue;
                }

                axes.TryGetValue(kind, out var counts);
                axes[kind] = (counts.All + 1, counts.Off + (offChips.Contains(claim) ? 1 : 0));
            }

            foreach (var axis in axes.Values)
            {
                if (axis.Off == axis.All)
                {
                    hidden.Add(node.Id);
                    break;
                }
            }
        }

        return hidden;
    }
}
