using CanonicalModel.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// The names a catalog's object ids stand for, handed to the writer together with the objects.
/// </summary>
/// <remarks>
/// <para>
/// A canonical object names its parent by <see cref="InterchangeId"/> and by nothing else, and an
/// adapter may not open a catalog to look an id up — so whoever read the catalog resolves the ids
/// before the objects cross the boundary. That is this record's whole job: it carries the answers
/// the reader already has, and nothing else.
/// </para>
/// <para>
/// A missing entry is a question the consumer did not answer, not a licence to guess: a writer that
/// cannot name an object's parent reports it rather than writing the name into the wrong schema.
/// </para>
/// </remarks>
/// <param name="Schemas">Schema names by schema id.</param>
/// <param name="Tables">Table identities by table id.</param>
public sealed record ObjectNameMap(
    IReadOnlyDictionary<InterchangeId, string> Schemas,
    IReadOnlyDictionary<InterchangeId, TableRef> Tables)
{
    /// <summary>A map holding nothing, for a caller with no catalog behind it.</summary>
    public static ObjectNameMap Empty { get; } =
        new(new Dictionary<InterchangeId, string>(), new Dictionary<InterchangeId, TableRef>());

    /// <summary>
    /// Names the schema an id stands for, or <see langword="null"/> when this map does not hold it.
    /// </summary>
    public string? SchemaName(InterchangeId id) =>
        Schemas.TryGetValue(id, out var name) ? name : null;

    /// <summary>
    /// Names the table an id stands for, or <see langword="null"/> when this map does not hold it.
    /// </summary>
    public TableRef? Table(InterchangeId id) =>
        Tables.TryGetValue(id, out var table) ? table : null;

    /// <summary>
    /// Names the schema a table lives in, or <see langword="null"/> when the table is unknown.
    /// </summary>
    public string? SchemaNameOfTable(InterchangeId id) => Table(id)?.Schema;
}
