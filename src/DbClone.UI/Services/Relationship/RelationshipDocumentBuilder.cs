using DbClone.Application.Relationship;
using DbClone.UI.Services.GraphLayout;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Builds the document from a graph: analyze, lay out, render — the three stages feature 07 has, ordered
/// once here so every caller produces the same document (D12).
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
    /// <returns>The origin the graph states and the whole document, held in memory.</returns>
    internal RelationshipDocument Build(RelationshipGraphView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var model = _analyzer.Analyze(view);
        var layout = _layout.Compute(model);

        return new RelationshipDocument(view.Origin, _renderer.Render(view, model, layout, DateTimeOffset.Now));
    }
}
