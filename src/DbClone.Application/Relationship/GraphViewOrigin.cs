namespace DbClone.Application.Relationship;

/// <summary>
/// Where a relationship graph came from. It replaces the catalog folder, format version and dialect
/// the view used to state, because half of the origins are no longer catalogs: the document's words,
/// its header and its title all follow this one record.
/// </summary>
/// <param name="Kind">Whether the graph was read from a catalog folder or a live database.</param>
/// <param name="Label">The display word: the catalog folder's name, or the database name.</param>
/// <param name="Location">
/// The tooltip: the catalog folder's absolute path, or <c>host:port/database</c>. Never a user and
/// never a password — neither is read into the view at all.
/// </param>
/// <param name="Dialect">The engine dialect: the catalog's recorded producer, or the live adapter's.</param>
/// <param name="FormatVersion">The interchange format version a catalog declares; <see langword="null"/> for a live read.</param>
public sealed record GraphViewOrigin(
    EGraphOriginKind Kind,
    string Label,
    string Location,
    string Dialect,
    int? FormatVersion);
