using System.Text;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The relationship register: every constraint the catalog holds, one row each, in full. The picture
/// prints no column text of its own (D4), so a constraint's columns are read here or in the panel —
/// and selecting a row highlights exactly its edge, which is how two constraints between one
/// pair stay telling apart (FR13, D7).
/// </summary>
/// <remarks>
/// The register never narrows. Direction focus and the cluster bar change what is displayed, not what
/// this table states, so a reader can always check the picture against the whole catalog.
/// </remarks>
internal sealed class GraphRegisterWriter
{
    private readonly CatalogGraphModel _model;

    /// <summary>Creates the writer for one document.</summary>
    /// <param name="model">The analyzed graph.</param>
    public GraphRegisterWriter(CatalogGraphModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    /// <summary>Writes the register.</summary>
    /// <param name="html">The document being built.</param>
    public void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        html.Append("<section class=\"register\">\n<h2>Relationship register \u2014 ")
            .Append(_model.RelationCount).Append(GraphFactText.Count(_model.RelationCount, " constraint", " constraints"))
            .Append("</h2>\n<div class=\"scroll\">\n<table>\n<thead><tr>")
            .Append("<th>#</th><th>constraint</th><th>from</th><th>to</th><th>columns</th>")
            .Append("<th>on delete</th><th>on update</th><th>rule</th></tr></thead>\n<tbody>\n");

        foreach (var edge in _model.Edges)
        {
            var child = _model.Nodes[edge.ChildIndex].DisplayName;
            var parent = _model.Nodes[edge.ParentIndex].DisplayName;
            html.Append("<tr data-e=\"").Append(edge.Index).Append("\">");
            html.Append("<td>").Append(edge.Index + 1).Append("</td>");
            html.Append("<td class=\"mono\">")
                .Append(GraphHtmlText.Escape(edge.Name.Length == 0 ? GraphFactText.Dash : edge.Name)).Append("</td>");
            html.Append("<td class=\"mono\">").Append(GraphHtmlText.Escape(child)).Append("</td>");
            html.Append("<td class=\"mono\">").Append(GraphHtmlText.Escape(parent)).Append("</td>");
            html.Append("<td class=\"mono\">").Append(GraphHtmlText.Escape(Pairs(edge))).Append("</td>");
            html.Append("<td>").Append(GraphHtmlText.Escape(GraphFactText.Lower(edge.OnDelete))).Append("</td>");
            html.Append("<td>").Append(GraphHtmlText.Escape(GraphFactText.Lower(edge.OnUpdate))).Append("</td>");
            html.Append("<td>").Append(GraphHtmlText.Escape(GraphFactText.Rule(edge))).Append("</td>");
            html.Append("</tr>\n");
        }

        html.Append("</tbody>\n</table>\n</div>\n</section>\n");
    }

    /// <summary>
    /// The column pairs as captured, one arrow each. Where the catalog states a column on one side
    /// only, the missing side is marked with a question mark rather than left to imply a match (D3).
    /// </summary>
    private static string Pairs(CatalogGraphEdge edge)
    {
        var pairs = new string[Math.Max(edge.Columns.Count, edge.ReferencedColumns.Count)];
        for (var index = 0; index < pairs.Length; index++)
        {
            var child = index < edge.Columns.Count ? edge.Columns[index] : "?";
            var parent = index < edge.ReferencedColumns.Count ? edge.ReferencedColumns[index] : "?";
            pairs[index] = $"{child} \u2192 {parent}";
        }

        return string.Join(", ", pairs);
    }
}
