using System.Globalization;
using System.Text;
using DbClone.Application.Interchange;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The document's opening region: what catalog this is, how much it holds, when the view was made,
/// and the key to every encoding the picture uses.
/// </summary>
/// <remarks>
/// The header states no figure the catalog folder cannot supply: there is no export instant (D2) and
/// there are no storage bytes (D3). The count line it opens with is the catalogue's own total, which
/// is also what the script restates whenever a filter narrows what is visible (FR7, D7).
/// </remarks>
internal sealed class GraphPageHeaderWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly CatalogGraphView _view;
    private readonly CatalogGraphModel _model;
    private readonly DateTimeOffset _generatedAt;

    /// <summary>Creates the writer for one document.</summary>
    /// <param name="view">The catalog as read back.</param>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="generatedAt">When the file was written.</param>
    public GraphPageHeaderWriter(
        CatalogGraphView view,
        CatalogGraphModel model,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(model);

        _view = view;
        _model = model;
        _generatedAt = generatedAt;
    }

    /// <summary>Writes the header and the legend.</summary>
    /// <param name="html">The document being built.</param>
    public void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        html.Append("<header>\n<h1>Database relationship explorer</h1>\n<p class=\"sub\">catalog <b title=\"")
            .Append(GraphHtmlText.Escape(_view.CatalogPath))
            .Append("\">").Append(GraphHtmlText.Escape(GraphHtmlText.FolderName(_view.CatalogPath))).Append("</b> \u00b7 ")
            .Append(_model.TableCount).Append(GraphFactText.Count(_model.TableCount, " table", " tables"))
            .Append(" \u00b7 ").Append(_model.RelationCount).Append(GraphFactText.Count(_model.RelationCount, " relation", " relations"))
            .Append(" \u00b7 format version ").Append(_view.FormatVersion)
            .Append(" \u00b7 ").Append(GraphHtmlText.Escape(_view.SourceDialect))
            .Append(" \u00b7 generated ").Append(_generatedAt.ToString("yyyy-MM-dd HH:mm", Invariant))
            .Append(_model.HasData ? string.Empty : " \u00b7 no row data was captured")
            .Append("</p>\n");

        html.Append("<p id=\"counts\">Showing ").Append(_model.TableCount).Append(" of ")
            .Append(_model.TableCount).Append(" tables \u00b7 ")
            .Append(_model.RelationCount).Append(" of ").Append(_model.RelationCount)
            .Append(" relations</p>\n");

        GraphLegendWriter.WriteTo(html);
        html.Append("</header>\n");
    }
}
