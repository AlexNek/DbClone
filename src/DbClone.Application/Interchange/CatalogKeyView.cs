namespace DbClone.Application.Interchange;

/// <summary>
/// A key a captured table states: its primary key or one of its unique constraints. Both carry a
/// name and the columns in the order the key itself declares them, which is not the table's column
/// order — a key over <c>(a, b)</c> is a different index from one over <c>(b, a)</c>.
/// </summary>
/// <param name="Name">The key's captured name, or an empty string when the catalog states none.</param>
/// <param name="Columns">The key's column names, in the key's own order.</param>
public sealed record CatalogKeyView(
    string Name,
    IReadOnlyList<string> Columns);
