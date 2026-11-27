using System.Globalization;
using System.Text;
using DbClone.Application.Relationship;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The document's opening region: what origin this is, how much it holds, when the view was made,
/// and the key to every encoding the picture uses.
/// </summary>
/// <remarks>
/// The header states no figure the origin cannot supply: there is no export instant (D2) and there
/// are no storage bytes (D3). The count line it opens with is the origin's own total, which is also
/// what the script restates whenever a filter narrows what is visible (FR7, D7).
/// </remarks>
internal sealed class GraphPageHeaderWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly RelationshipGraphView _view;
    private readonly CatalogGraphModel _model;
    private readonly DateTimeOffset _generatedAt;
    private readonly GraphOriginWords _words;

    /// <summary>Creates the writer for one document.</summary>
    /// <param name="view">The origin as read back.</param>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="generatedAt">When the file was written.</param>
    public GraphPageHeaderWriter(
        RelationshipGraphView view,
        CatalogGraphModel model,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(model);

        _view = view;
        _model = model;
        _generatedAt = generatedAt;
        _words = GraphOriginWords.For(model.Origin.Kind);
    }

    /// <summary>Writes the header and the legend.</summary>
    /// <param name="html">The document being built.</param>
    public void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var origin = _view.Origin;
        html.Append("<header>\n<h1>Database relationship explorer</h1>\n<p class=\"sub\">")
            .Append(GraphHtmlText.Escape(_words.HeaderLead))
            .Append(" <b title=\"").Append(GraphHtmlText.Escape(origin.Location))
            .Append("\">").Append(GraphHtmlText.Escape(origin.Label)).Append("</b> \u00b7 ")
            .Append(_model.TableCount).Append(GraphFactText.Count(_model.TableCount, " table", " tables"))
            .Append(" \u00b7 ").Append(_model.RelationCount).Append(GraphFactText.Count(_model.RelationCount, " relation", " relations"));

        if (origin.FormatVersion is { } version)
        {
            html.Append(" \u00b7 format version ").Append(version);
        }

        html.Append(" \u00b7 ").Append(GraphHtmlText.Escape(origin.Dialect))
            .Append(" \u00b7 ").Append(_words.TimestampWord).Append(' ')
            .Append(_generatedAt.ToString("yyyy-MM-dd HH:mm", Invariant))
            .Append(_model.HasData ? string.Empty : _words.HeaderNoData)
            .Append("</p>\n");

        html.Append("<p id=\"counts\">Showing ").Append(_model.TableCount).Append(" of ")
            .Append(_model.TableCount).Append(" tables \u00b7 ")
            .Append(_model.RelationCount).Append(" of ").Append(_model.RelationCount)
            .Append(" relations</p>\n");

        GraphLegendWriter.WriteTo(html, _words);
        html.Append("</header>\n");
    }
}
