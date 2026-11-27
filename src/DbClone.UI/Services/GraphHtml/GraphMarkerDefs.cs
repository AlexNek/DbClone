using System.Text;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The marks an arrow carries, written once per document so the picture and its key draw one shape
/// rather than two near-enough copies of it.
/// </summary>
/// <remarks>
/// <para>
/// The block sits in a zero-sized svg before the header, outside the viewport the reader pans: a
/// definition inside the viewport would travel and scale with it, and a key whose sample differs from
/// the arrow it names teaches the reader to distrust both.
/// </para>
/// <para>
/// Only the child end carries a mark, and only where it states the exception. A foreign key always
/// references one row, so the parent end says nothing that varies; and many child rows naming one
/// parent row is the case a catalog is full of, so drawing a fork on it would repeat the default on
/// every line instead of stating a fact the reader could not otherwise know. A bar is therefore the
/// whole vocabulary: it means the child has at most one row per parent row, and no bar means the
/// catalog does not prove that (D14.1, D14.4).
/// </para>
/// <para>
/// Both marks are referenced from the start of the path with no backward reach (refX="0") and turned
/// with it (orient="auto", never auto-start-reverse, which would face them the wrong way). A route
/// begins on the card's own border and the cards are painted after the edges, so a mark centred on the
/// start vertex would be covered by the card it belongs to; hanging it off the line instead takes it
/// along with the line whenever a filter or a collapse hides that line, with no second code path.
/// </para>
/// </remarks>
internal static class GraphMarkerDefs
{
    /// <summary>Writes the marks the picture's arrows and the key's samples reference.</summary>
    /// <param name="html">The document being built.</param>
    internal static void WriteTo(StringBuilder html)
    {
        ArgumentNullException.ThrowIfNull(html);

        html.Append("<svg class=\"marks\" width=\"0\" height=\"0\" aria-hidden=\"true\" focusable=\"false\"><defs>")
            .Append("<marker id=\"head\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" ")
            .Append("markerWidth=\"9\" markerHeight=\"9\" orient=\"auto-start-reverse\">")
            .Append("<path d=\"M 0 0 L 10 5 L 0 10 z\" fill=\"#8b949e\"/></marker>")
            .Append("<marker id=\"one\" viewBox=\"0 0 10 10\" refX=\"0\" refY=\"5\" ")
            .Append("markerWidth=\"8\" markerHeight=\"8\" orient=\"auto\">")
            .Append("<path d=\"M 7 0 L 7 10\" stroke=\"#8b949e\" stroke-width=\"2.5\" fill=\"none\"/></marker>")
            .Append("</defs></svg>\n");
    }
}
