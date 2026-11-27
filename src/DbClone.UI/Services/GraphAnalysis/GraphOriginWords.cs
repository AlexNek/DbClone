using DbClone.Application.Relationship;

namespace DbClone.UI.Services.GraphAnalysis;

/// <summary>
/// Every word the document says about an origin, stated once. The C# half and the script half read the
/// same instance, so a card, a panel, a legend row and a register column cannot describe one fact two
/// ways, and a live read cannot inherit a catalog's vocabulary.
/// </summary>
/// <remarks>
/// One instance per <see cref="EGraphOriginKind"/>, created once and never mutated: a document is a
/// function of its origin, and the words are part of that function.
/// </remarks>
internal sealed class GraphOriginWords
{
    private GraphOriginWords(
        EGraphOriginKind kind,
        string headerLead,
        string timestampWord,
        string headerNoData,
        string rowsNote,
        string outsideNote,
        string unnamedReference,
        string outsideRule,
        string legendOutside,
        string searchLabel)
    {
        Kind = kind;
        HeaderLead = headerLead;
        TimestampWord = timestampWord;
        HeaderNoData = headerNoData;
        RowsNote = rowsNote;
        OutsideNote = outsideNote;
        UnnamedReference = unnamedReference;
        OutsideRule = outsideRule;
        LegendOutside = legendOutside;
        SearchLabel = searchLabel;
    }

    /// <summary>Gets the origin kind these words belong to.</summary>
    internal EGraphOriginKind Kind { get; }

    /// <summary>Gets the word before the origin's name in the header: <c>catalog</c> or <c>live database</c>.</summary>
    internal string HeaderLead { get; }

    /// <summary>Gets the word before the header's timestamp: <c>generated</c> or <c>read</c>.</summary>
    internal string TimestampWord { get; }

    /// <summary>Gets the header's whole missing-data clause, separator included, or an empty string when the header stays silent.</summary>
    internal string HeaderNoData { get; }

    /// <summary>
    /// Gets the panel's and the card's line for a table the origin states no row count for, or an empty
    /// string when the origin says nothing about rows table by table and the header carries the reason.
    /// </summary>
    internal string RowsNote { get; }

    /// <summary>Gets the panel's and the card's line for a table the origin does not hold.</summary>
    internal string OutsideNote { get; }

    /// <summary>Gets the words for a reference that states no table name at all.</summary>
    internal string UnnamedReference { get; }

    /// <summary>Gets the register's rule column for a constraint whose parent is not held.</summary>
    internal string OutsideRule { get; }

    /// <summary>Gets the legend row for a dashed arrow.</summary>
    internal string LegendOutside { get; }

    /// <summary>Gets the search field's accessible label.</summary>
    internal string SearchLabel { get; }

    /// <summary>Returns the words an origin kind is stated in.</summary>
    /// <param name="kind">The origin kind.</param>
    /// <returns>The one instance for that kind.</returns>
    internal static GraphOriginWords For(EGraphOriginKind kind) =>
        kind == EGraphOriginKind.LiveDatabase ? Live : Catalog;

    private static readonly GraphOriginWords Catalog = new(
        EGraphOriginKind.Catalog,
        headerLead: "catalog",
        timestampWord: "generated",
        headerNoData: " \u00b7 no row data was captured",
        rowsNote: "no data captured",
        outsideNote: "not in this catalog",
        unnamedReference: "a table the catalog does not name",
        outsideRule: "outside the catalog",
        legendOutside: "the table it points at is not in this catalog",
        searchLabel: "Search the catalog");

    private static readonly GraphOriginWords Live = new(
        EGraphOriginKind.LiveDatabase,
        headerLead: "live database",
        timestampWord: "read",
        // A live read counts no rows for any table, so the reason is a fact about the origin and is
        // stated once in the header — a card prints nothing it did not measure (plan D1, D2).
        headerNoData: " \u00b7 rows are not counted here",
        rowsNote: string.Empty,
        outsideNote: "not in this view",
        unnamedReference: "a table the database does not name",
        outsideRule: "outside this view",
        legendOutside: "the table it points at is not in this view",
        searchLabel: "Search this view");
}
