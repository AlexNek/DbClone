namespace DbClone.Application.Interchange;

/// <summary>
/// The SQL scripts a store wrote beside its catalog, as the application reports them: the schema file, the
/// data files, the driver that includes them, and what they hold.
/// </summary>
/// <remarks>
/// <para>
/// The product hands back paths relative to the catalog root, because it is the one that names its own
/// files; this record carries the absolute paths the application formed from them, so no consumer above
/// this layer needs to know a folder or file name (FR16).
/// </para>
/// <para>
/// <see cref="SchemaPath"/> is <see langword="null"/> exactly when nothing was written — the option was
/// off, the pass refused, the write failed, or the user cancelled. The reason arrives as an issue beside
/// this record, never as an exception and never as a failed store. <see cref="DataFiles"/> is empty on its
/// own when the catalog held no rows: a schema with no data is a complete answer.
/// </para>
/// <para>
/// <see cref="LoadPath"/> is present only when the pass wrote more than one data file. It never carries a
/// statement: running it is the caller's decision, and everything it includes may be run twice.
/// </para>
/// </remarks>
public sealed record SqlScriptPaths(
    string? SchemaPath,
    IReadOnlyList<SqlScriptFile> DataFiles,
    string? LoadPath,
    long SchemaBytes,
    int SchemaStatements,
    long Rows,
    long PayloadBytes)
{
    /// <summary>Gets whether a schema script was written.</summary>
    public bool Written => SchemaPath is not null;

    /// <summary>Gets whether the rows were written as more than one file.</summary>
    public bool IsFamily => DataFiles.Count > 1;
}
