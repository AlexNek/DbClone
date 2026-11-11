using System.Text;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The key to the picture: one row per thing the reader meets on it, drawn as it is drawn there.
/// </summary>
/// <remarks>
/// <para>
/// A row shows the shape, then the name the industry gives it, then one plain-English sentence. The two
/// cardinality rows lead with the relationship name every ER editor and textbook shares —
/// many-to-one (N:1), one-to-one (1:1) — because that is the term a reader already knows, and follow it
/// with a sentence for the reader who does not: the words name the two tables by what the arrow does
/// (the source it starts from, the target it points at), so they can be checked against the line beside
/// them instead of being translated from some role. The bar states a maximum and never "exactly one":
/// whether a relationship is optional or mandatory is decided by a value or a trigger, which the
/// catalog does not carry. The row is the conclusion and nothing else: a paragraph beside a 48-pixel
/// sample turns a key into an article, and the reasoning each row leaves out is the manual's job and
/// the panel's, not the key's.
/// </para>
/// <para>
/// Every sample references the marks the arrows themselves carry, so the key and the picture cannot
/// drift apart, and no row states a figure the catalog could contradict.
/// </para>
/// <para>
/// The key hangs under the header closed: seven rows of prose cost the picture a fifth of the screen,
/// and a reader who has learnt the marks should not pay for them on every open. It is a native
/// disclosure, so it needs no script to open and keeps its own keyboard behaviour and its own print
/// rules, and every row stays in the document whether it is shown or not.
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

        html.Append("<details class=\"keyed\">\n")
            .Append("<summary>Key \u2014 how to read the picture</summary>\n")
            .Append("<ul class=\"legend\">\n");
        Row(html, Arrow, "many-to-one (N:1)",
            "multiple records in the source table can reference the same record in the target table");
        Row(html, Bar, "one-to-one (1:1)",
            "each record in the target table can be referenced by at most one record in the source table");
        Row(html, Dashed, "dashed", "the table it points at is not in this catalog");
        Row(html, Curve, "curve", "a cycle: the two tables point at each other");
        Row(html, Loop, "loop", "the table points at itself");
        Row(html, Card, "card", "one table: its name and row count");
        Row(html, Collapsed, "collapsed", "a cyclic group as one box; select to expand");
        html.Append("</ul>\n</details>\n");
    }

    private static void Row(StringBuilder html, string sample, string word, string meaning)
    {
        html.Append("<li>").Append(sample).Append("<b>")
            .Append(GraphHtmlText.Escape(word)).Append("</b> ")
            .Append(GraphHtmlText.Escape(meaning)).Append("</li>\n");
    }
}
