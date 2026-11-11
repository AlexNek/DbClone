using System.Text;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The key to the picture: one row per thing the reader meets on it, drawn as it is drawn there.
/// </summary>
/// <remarks>
/// <para>
/// A row shows the shape first and then states what may be concluded from it. A key that only names
/// shapes asks the reader to picture a mark before being allowed to see one, and a sentence that
/// states a cardinality in the abstract — "many rows to one row" — has to be read against the arrow to
/// be decoded at all, because it never says which end is the many one.
/// </para>
/// <para>
/// Every sample references the marks the arrows themselves carry, so the key and the picture cannot
/// drift apart, and no row states a figure the catalog could contradict.
/// </para>
/// </remarks>
internal static class GraphLegendWriter
{
    private const string Arrow = "<svg class=\"key\" viewBox=\"0 0 48 16\" aria-hidden=\"true\">"
        + "<path class=\"edge\" d=\"M 4 8 L 40 8\" marker-end=\"url(#head)\"/></svg>";

    private const string Bar = "<svg class=\"key\" viewBox=\"0 0 48 16\" aria-hidden=\"true\">"
        + "<path class=\"edge\" d=\"M 9 8 L 40 8\" marker-start=\"url(#one)\" marker-end=\"url(#head)\"/></svg>";

    private const string Dashed = "<svg class=\"key\" viewBox=\"0 0 48 16\" aria-hidden=\"true\">"
        + "<path class=\"edge out\" d=\"M 4 8 L 40 8\" marker-end=\"url(#head)\"/></svg>";

    private const string Curve = "<svg class=\"key\" viewBox=\"0 0 48 16\" aria-hidden=\"true\">"
        + "<path class=\"edge\" d=\"M 4 13 C 16 2, 32 2, 44 13\" marker-end=\"url(#head)\"/></svg>";

    private const string Loop = "<svg class=\"key\" viewBox=\"0 0 48 16\" aria-hidden=\"true\">"
        + "<rect x=\"2\" y=\"3\" width=\"9\" height=\"10\" rx=\"2\"/>"
        + "<path class=\"edge\" d=\"M 11 6 C 40 6, 40 13, 11 13\" marker-end=\"url(#head)\"/></svg>";

    private const string Card = "<svg class=\"key\" viewBox=\"0 0 48 16\" aria-hidden=\"true\">"
        + "<rect x=\"2\" y=\"2\" width=\"34\" height=\"12\" rx=\"3\"/></svg>";

    private const string Collapsed = "<svg class=\"key\" viewBox=\"0 0 48 16\" aria-hidden=\"true\">"
        + "<rect class=\"super\" x=\"2\" y=\"2\" width=\"34\" height=\"12\" rx=\"3\"/></svg>";

    /// <summary>Writes the key's rows.</summary>
    /// <param name="html">The document being built.</param>
    internal static void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        html.Append("<ul class=\"legend\">\n");
        Row(html, Arrow, "arrow",
            "leaves the table that stores the column and points at the table the value comes from. "
            + "Nothing at the tail: many rows there may name the one row at the head");
        Row(html, Bar, "bar",
            "at most one row of this table for each row it points at \u2014 a one-to-one extension. "
            + "Only a key on exactly the referencing columns proves it, so no bar means many rows, or a "
            + "pair the catalog cannot prove");
        Row(html, Dashed, "dashed",
            "the table the arrow points at is not in this catalog, so no card stands where it lands");
        Row(html, Curve, "curve",
            "the constraint closes a cycle, so these tables point at one another and the line bows "
            + "instead of running straight");
        Row(html, Loop, "loop",
            "the table points at itself: a row's value comes from another row of the same table");
        Row(html, Card, "card",
            "one per table, with its name and its row count printed inside it");
        Row(html, Collapsed, "collapsed",
            "a cyclic group drawn as one box; select it and the tables it holds open where they stand");
        html.Append("</ul>\n");
    }

    private static void Row(StringBuilder html, string sample, string word, string meaning)
    {
        html.Append("<li>").Append(sample).Append("<b>")
            .Append(GraphHtmlText.Escape(word)).Append("</b> ")
            .Append(GraphHtmlText.Escape(meaning)).Append("</li>\n");
    }
}
