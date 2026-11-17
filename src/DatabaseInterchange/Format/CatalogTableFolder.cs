namespace DatabaseInterchange.Format;

/// <summary>
/// The data folder one table's rows were written to, recorded with the name the catalog states for it.
/// </summary>
/// <remarks>
/// <para>
/// The pair <see cref="Schema"/>/<see cref="Table"/> is the table as its own section names it; <see cref="Folder"/>
/// is the path the writer actually created, relative to the catalog's <c>data/</c> directory. Recording the result
/// is what makes a collision-resolved folder readable again: the loser of a case collision lives under a
/// discriminator tail that no reader may re-derive from the table's name, because the hash input is the sibling
/// list that produced it. The folder name is therefore written once, at export, and looked up afterwards.
/// </para>
/// </remarks>
public sealed record CatalogTableFolder
{
    /// <summary>Gets the schema name as the catalog's tables section states it.</summary>
    public string Schema { get; init; } = string.Empty;

    /// <summary>Gets the table name as the catalog's tables section states it.</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>
    /// Gets the folder holding the table's data, relative to <c>data/</c>, with forward slashes:
    /// <c>&lt;schema segment&gt;/&lt;table segment&gt;</c>.
    /// </summary>
    public string Folder { get; init; } = string.Empty;
}
