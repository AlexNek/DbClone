namespace DbClone.Application.Interchange;

/// <summary>
/// The SQL scripts a store wrote beside its catalog, as the application reports them: the two absolute
/// paths, their sizes, and what they hold.
/// </summary>
/// <remarks>
/// <para>
/// The product hands back paths relative to the catalog root, because it is the one that names its own
/// files; this record carries the absolute paths the application formed from them, so no consumer above
/// this layer needs to know a folder or file name (FR13).
/// </para>
/// <para>
/// <see cref="SchemaPath"/> is <see langword="null"/> exactly when nothing was written — the option was
/// off, the pass refused, the write failed, or the user cancelled. The reason arrives as an issue beside
/// this record, never as an exception and never as a failed store. <see cref="DataPath"/> is null on its own
/// when the catalog held no rows: a schema with no data is a complete answer.
/// </para>
/// </remarks>
public sealed record SqlScriptPaths(
    string? SchemaPath,
    string? DataPath,
    long SchemaBytes,
    long DataBytes,
    int SchemaStatements,
    int DataStatements,
    long Rows,
    long PayloadBytes)
{
    /// <summary>Gets whether a schema script was written.</summary>
    public bool Written => SchemaPath is not null;
}
