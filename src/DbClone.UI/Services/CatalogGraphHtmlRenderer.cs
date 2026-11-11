using System.Text;
using DbClone.Application.Interchange;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphHtml;
using DbClone.UI.Services.GraphLayout;

namespace DbClone.UI.Services;

/// <summary>
/// The emit stage: writes the analyzed graph and its coordinates as one self-contained HTML document.
/// It formats, escapes and emits; every decision it makes has already been made by the analyzer or
/// the layout pass, and it never reads a file, a clock or a culture.
/// </summary>
/// <remarks>
/// <para>
/// This class is a coordinator only: it writes the document's shell and asks one writer per region
/// for that region's markup — the header and legend, the cluster bar, the workspace with its stage
/// and panel, the register, and the data island. Each region has its own writer, so a change to the
/// register cannot reach the picture and a change to the island cannot reach the markup (D12).
/// </para>
/// <para>
/// No catalog string reaches markup as markup: names, comments and constraint text go through
/// <see cref="GraphHtmlText.Escape(string)"/> in the body and through the JSON encoder inside the data
/// island, and the script addresses elements by number rather than by name.
/// </para>
/// <para>
/// The document is a pure function of its four arguments, so the same catalog produces the same bytes
/// twice — which is what makes two views of one catalog comparable and a diff of two views readable.
/// </para>
/// </remarks>
internal sealed class CatalogGraphHtmlRenderer
{
    /// <summary>Writes the whole document.</summary>
    /// <param name="view">The catalog as read back, for the facts the graph itself does not carry.</param>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="layout">The computed picture.</param>
    /// <param name="generatedAt">When the file was written, passed in so the document stays a function of its arguments.</param>
    /// <returns>The document, as text.</returns>
    public string Render(
        CatalogGraphView view,
        CatalogGraphModel model,
        CatalogGraphLayout layout,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layout);

        var html = new StringBuilder(1 << 16);
        WriteHead(html, view);

        // The marks are defined once, for the whole document: the arrows in the viewport and the samples
        // in the key reference the same shapes, so the key cannot promise a mark the picture never draws.
        GraphMarkerDefs.WriteTo(html);

        new GraphPageHeaderWriter(view, model, generatedAt).WriteTo(html);
        new GraphClusterBarWriter(model).WriteTo(html);
        new GraphWorkspaceWriter(model, layout, new GraphPictureWriter(model, layout)).WriteTo(html);
        new GraphRegisterWriter(model).WriteTo(html);
        new GraphIslandWriter(view, model).WriteTo(html);
        WriteScript(html);

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    private static void WriteHead(StringBuilder html, CatalogGraphView view)
    {
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>Relationships \u2014 ")
            .Append(GraphHtmlText.Escape(GraphHtmlText.FolderName(view.CatalogPath)))
            .Append("</title>\n");
        html.Append("<style>\n").Append(CatalogGraphStyles.Css).Append("\n</style>\n");
        html.Append("</head>\n<body>\n");
    }

    private static void WriteScript(StringBuilder html) =>
        html.Append("<script>\n").Append(CatalogGraphScript.Javascript).Append("\n</script>\n");
}
