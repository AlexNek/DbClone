using System.Text;
using DbClone.Application.Relationship;
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
/// The document is a pure function of its four arguments, so the same origin produces the same bytes
/// twice — which is what makes two views of one origin comparable and a diff of two views readable.
/// </para>
/// </remarks>
internal sealed class CatalogGraphHtmlRenderer
{
    /// <summary>Writes the whole document.</summary>
    /// <param name="view">The origin as read back, for the facts the graph itself does not carry.</param>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="layout">The computed picture.</param>
    /// <param name="generatedAt">When the file was written, passed in so the document stays a function of its arguments.</param>
    /// <param name="whole">
    /// The analyzed whole catalog when <paramref name="model"/> is the picture of a switched-off
    /// selection: the chip bar and the totals are stated from it, so the reader keeps every cluster to
    /// switch back on. <see langword="null"/> when the picture is the whole catalog.
    /// </param>
    /// <param name="offChips">
    /// The chips the reader switched off, which the bar prints as off so the document states the
    /// selection it was drawn for. <see langword="null"/> when nothing is switched off.
    /// </param>
    /// <returns>The document, as text.</returns>
    public string Render(
        RelationshipGraphView view,
        CatalogGraphModel model,
        CatalogGraphLayout layout,
        DateTimeOffset generatedAt,
        CatalogGraphModel? whole = null,
        IReadOnlySet<int>? offChips = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layout);

        var bar = whole ?? model;
        var off = offChips ?? NoChips;

        var html = new StringBuilder(1 << 16);
        WriteHead(html, view);

        // The marks are defined once, for the whole document: the arrows in the viewport and the samples
        // in the key reference the same shapes, so the key cannot promise a mark the picture never draws.
        GraphMarkerDefs.WriteTo(html);

        new GraphPageHeaderWriter(view, model, generatedAt).WriteTo(html);
        new GraphClusterBarWriter(bar, off).WriteTo(html);
        new GraphWorkspaceWriter(model, new GraphPictureWriter(model, layout, Claims(model, off))).WriteTo(html);
        new GraphRegisterWriter(model).WriteTo(html);
        new GraphIslandWriter(view, model, bar, off).WriteTo(html);
        WriteScript(html);

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>The selection a whole-catalog picture is drawn for: no chip is switched off.</summary>
    private static readonly HashSet<int> NoChips = [];

    /// <summary>What a picture drawn for a selection prints as its cards' claims: nothing.</summary>
    private static readonly Dictionary<int, IReadOnlyList<int>> NoClaims = [];

    /// <summary>
    /// The claims the cards carry. A picture drawn for a selection already holds only the clusters that
    /// are on, so its cards claim nothing: the script's own filter then leaves every card standing
    /// instead of hiding a table twice, and the bar's chips keep the whole catalog's numbers (T28).
    /// </summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<int>> Claims(CatalogGraphModel model, IReadOnlySet<int> off) =>
        off.Count == 0 ? model.ChipIndexesByNode : NoClaims;

    private static void WriteHead(StringBuilder html, RelationshipGraphView view)
    {
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>Relationships \u2014 ")
            .Append(GraphHtmlText.Escape(view.Origin.Label))
            .Append("</title>\n");
        html.Append("<style>\n").Append(CatalogGraphStyles.Css).Append("\n</style>\n");
        html.Append("</head>\n<body>\n");
    }

    private static void WriteScript(StringBuilder html) =>
        html.Append("<script>\n").Append(CatalogGraphScript.Javascript).Append("\n</script>\n");
}
