using System.Globalization;
using DbClone.UI.Models.CatalogGraph;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// The words a table's and a constraint's facts are printed in — the card's two lines, its tooltip, and
/// the register's rule column. One wording per fact, so the picture and the register cannot describe
/// the same thing two ways.
/// </summary>
/// <remarks>
/// The decisions behind these words were made by the analyzer (a table may be outside the catalog, a
/// row count may be absent, a constraint may be mandatory); this class only spells them out. It sits in
/// the analysis folder because the layout measures the very line the renderer prints, and measuring a
/// copy of that wording would let the two drift apart.
/// </remarks>
internal static class GraphFactText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The mark printed where a fact has no value at all — never a zero and never a blank.</summary>
    internal const string Dash = "\u2014";

    /// <summary>Picks the singular or the plural form of a noun for a count.</summary>
    /// <param name="count">The count being described.</param>
    /// <param name="one">The singular form.</param>
    /// <param name="many">The plural form.</param>
    /// <returns>The form that agrees with the count.</returns>
    internal static string Count(long count, string one, string many) => count == 1 ? one : many;

    /// <summary>Writes a row count with its thousands separators and no culture of its own.</summary>
    /// <param name="rows">The row count the catalog captured.</param>
    /// <returns>The number as a reader expects to read it.</returns>
    internal static string Rows(long rows) => rows.ToString("N0", Invariant);

    /// <summary>Lower-cases a captured action, or states that none was captured.</summary>
    /// <param name="value">The captured action, if any.</param>
    /// <returns>The action in lower case, or <see cref="Dash"/>.</returns>
    internal static string Lower(string? value) =>
        string.IsNullOrEmpty(value) ? Dash : value.ToLowerInvariant();

    /// <summary>
    /// The card's second line: the row count, the absence of data, or the fact that the catalog does
    /// not hold the table at all. A table the catalog does not hold is never given a figure it does not
    /// have (FR4, D7).
    /// </summary>
    /// <param name="node">The table.</param>
    /// <param name="words">The origin's words.</param>
    /// <returns>The one line printed under the table's name.</returns>
    internal static string NodeFacts(CatalogGraphNode node, GraphOriginWords words)
    {
        if (!node.IsCaptured)
        {
            return words.OutsideNote;
        }

        if (node.RowCount is not { } rows)
        {
            return words.RowsNote;
        }

        return $"{Rows(rows)} {Count(rows, "row", "rows")}";
    }

    /// <summary>
    /// The card's tooltip: the name, the facts, both direction counts, which are never merged into one
    /// figure (FR12, D7), and the words that name a link table, marked as the inference it is (D14.3).
    /// </summary>
    /// <param name="node">The table.</param>
    /// <param name="words">The origin's words.</param>
    /// <returns>The tooltip text.</returns>
    internal static string NodeTitle(CatalogGraphNode node, GraphOriginWords words)
    {
        var state = node.IsCaptured ? NodeFacts(node, words) : words.OutsideNote;
        var join = node.JoinTableBetween is null ? string.Empty : $" \u2014 {node.JoinTableBetween} (inferred)";
        return $"{node.DisplayName} \u2014 {state} \u2014 references {node.DegreeOut}, referenced by {node.DegreeIn}{join}";
    }

    /// <summary>
    /// The register's rule column: a self reference, a reference that leaves the catalog, or the
    /// declared delete behaviour spelled out rather than only styled.
    /// </summary>
    /// <param name="edge">The constraint.</param>
    /// <param name="words">The origin's words.</param>
    /// <returns>The rule in words.</returns>
    internal static string Rule(CatalogGraphEdge edge, GraphOriginWords words)
    {
        if (edge.IsSelfReference)
        {
            return "self reference";
        }

        if (!edge.ParentIsCaptured)
        {
            return words.OutsideRule;
        }

        return edge.IsMandatory ? "mandatory" : "optional";
    }
}
