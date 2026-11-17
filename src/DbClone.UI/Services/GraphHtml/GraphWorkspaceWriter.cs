using System.Text;
using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The workspace: the toolbar that narrows what is in focus, the pinned stage that holds the picture,
/// and the panel that answers for whatever is selected.
/// </summary>
/// <remarks>
/// The stage is a fixed-size box and the picture is drawn in pixels inside it, so the toolbar's zoom
/// buttons pan and scale rather than replacing a scaled <c>viewBox</c> — which is what makes a
/// twenty-eight-table drawing readable instead of a thumbnail.
/// </remarks>
internal sealed class GraphWorkspaceWriter
{
    private readonly CatalogGraphModel _model;
    private readonly GraphPictureWriter _picture;
    private readonly GraphOriginWords _words;

    /// <summary>Creates the writer for one document.</summary>
    /// <param name="model">The analyzed graph.</param>
    /// <param name="picture">The writer of what goes inside the viewport.</param>
    public GraphWorkspaceWriter(
        CatalogGraphModel model,
        GraphPictureWriter picture)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(picture);

        _model = model;
        _picture = picture;
        _words = GraphOriginWords.For(model.Origin.Kind);
    }

    /// <summary>Writes the toolbar, the stage and the panel.</summary>
    /// <param name="html">The document being built.</param>
    public void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        html.Append("<div class=\"layout\">\n<main>\n<div id=\"toolbar\" class=\"toolbar\">\n");
        WriteSearchField(html);
        WriteDirectionFieldset(html);
        WriteZoomButtons(html);
        html.Append("</div>\n<div id=\"matches\" hidden></div>\n");

        WriteStage(html);

        html.Append("</main>\n");
        WritePanel(html);
        html.Append("</div>\n");
    }

    private void WriteSearchField(StringBuilder html) =>
        html.Append("<label>Search <input id=\"query\" type=\"search\" autocomplete=\"off\" ")
            .Append("placeholder=\"table, column, constraint or comment\" aria-label=\"")
            .Append(GraphHtmlText.Escape(_words.SearchLabel)).Append("\"></label>\n");

    private void WriteDirectionFieldset(StringBuilder html)
    {
        html.Append("<fieldset><legend>Direction</legend>\n");
        DirectionOption(html, "both", "both ways", true);
        DirectionOption(html, "out", "references", false);
        DirectionOption(html, "in", "referenced by", false);
        html.Append("</fieldset>\n");
    }

    private static void DirectionOption(StringBuilder html, string value, string caption, bool isChecked)
    {
        html.Append("<label><input type=\"radio\" name=\"direction\" value=\"").Append(value).Append('"')
            .Append(isChecked ? " checked" : string.Empty).Append("> ")
            .Append(GraphHtmlText.Escape(caption)).Append("</label>\n");
    }

    private void WriteZoomButtons(StringBuilder html)
    {
        if (!_model.InitialView.ShowsWholeGraph)
        {
            html.Append("<button type=\"button\" id=\"showall\">Show the whole database</button>\n");
        }

        html.Append("<button type=\"button\" data-zoom=\"out\" aria-label=\"Zoom out\">\u2212</button>\n");
        html.Append("<button type=\"button\" data-zoom=\"in\" aria-label=\"Zoom in\">+</button>\n");
        html.Append("<button type=\"button\" data-zoom=\"fit\">Fit</button>\n");
        html.Append("<button type=\"button\" data-zoom=\"reset\">100%</button>\n");
        html.Append("<span class=\"meta\">drag a table to move it \u00b7 drag the background to pan \u00b7 wheel to zoom</span>\n");
    }

    private void WriteStage(StringBuilder html)
    {
        // The picture is sized by the stage, never by the canvas: the svg is the one box the browser
        // clips to, so a canvas narrower or shorter than the stage would slice the drawing the camera
        // centres in it.
        html.Append("<div class=\"stage\">\n<svg id=\"pic\" role=\"img\" aria-label=\"")
            .Append(GraphHtmlText.Escape($"{_model.TableCount} tables and {_model.RelationCount} constraints"))
            .Append("\">\n");

        _picture.WriteTo(html);

        html.Append("</svg>\n</div>\n");
    }

    /// <summary>
    /// The panel's opening state. Its contents are written by the script, which reads the data island
    /// rather than a second copy of the facts.
    /// </summary>
    private static void WritePanel(StringBuilder html)
    {
        html.Append("<aside>\n<div id=\"panel\"><p class=\"meta\">Select a table to see its columns, ")
            .Append("its key, indexes and checks, its references and the tables that point at it.</p></div>\n");
        html.Append("</aside>\n");
    }
}
