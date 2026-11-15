using DbClone.UI.Models.CatalogGraph;
using DbClone.UI.Services.GraphAnalysis;

namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The card a table is drawn as: how wide its own two lines need it to be, and where those lines sit
/// inside it. The layout reserves the space and the renderer paints it, so both read the measure from
/// here rather than estimating it twice.
/// </summary>
/// <remarks>
/// The width follows the text and nothing else — no dimension states a figure (plan D4, pass 6). The
/// character widths are set slightly above what the document's own font measures at 12px and 11px
/// (the widest glyph ratio seen on a rendered catalog was 6.36 for a name and 5.96 for a count), so a
/// card errs a little wide rather than letting a letter lean into its border; the layout is not a text
/// engine, and a name of unusual glyphs is answered in full by the tooltip and the panel anyway. The
/// one figure a card may have to answer is the number of arrows that meet it: the layout takes the
/// larger of this measure and the width their ends need on the border (GraphEdgePorts.WidthFor), so a
/// hub is drawn wide rather than printing two arrow heads on one place.
/// </remarks>
internal static class GraphCard
{
    /// <summary>The height every card is drawn at: room for the name and the count, nothing more.</summary>
    internal const double Height = 40;

    /// <summary>The gap between a card's border and its text, on either side.</summary>
    internal const double Padding = 9;

    /// <summary>The width a card never goes below, so a very short name still reads as a box.</summary>
    internal const double MinWidth = 72;

    /// <summary>The width one character of a name takes at the size the document prints it.</summary>
    internal const double NameCharWidth = 7.0;

    /// <summary>The width one character of the count line takes, at its smaller size.</summary>
    internal const double FactCharWidth = 6.2;

    /// <summary>The baseline the name is printed on, measured down from the card's top edge.</summary>
    internal const double NameBaseline = 17;

    /// <summary>The baseline the count is printed on, measured down from the card's top edge.</summary>
    internal const double FactBaseline = 32;

    /// <summary>The width the card of a table needs, from the two lines printed inside it.</summary>
    /// <param name="node">The table.</param>
    /// <param name="words">The origin's words, so the measured line is the printed line.</param>
    /// <returns>The card's width.</returns>
    internal static double WidthOf(CatalogGraphNode node, GraphOriginWords words)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(words);

        return Width(node.DisplayName, GraphFactText.NodeFacts(node, words));
    }

    /// <summary>The width two printed lines need, with the card's padding on either side.</summary>
    /// <param name="name">The name printed on the first line.</param>
    /// <param name="facts">The count printed on the second line.</param>
    /// <returns>The card's width.</returns>
    internal static double Width(string name, string facts)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(facts);

        var text = Math.Max(TextWidth(name, NameCharWidth), TextWidth(facts, FactCharWidth));
        return Math.Max(MinWidth, text + (2 * Padding));
    }

    private static double TextWidth(string text, double charWidth) => text.Length * charWidth;
}
