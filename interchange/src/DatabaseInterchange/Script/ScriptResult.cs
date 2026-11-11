namespace DatabaseInterchange.Script;

/// <summary>
/// What a script pass produced: the two files' paths and sizes, what they hold, and why nothing was written
/// when nothing was (D8).
/// </summary>
/// <remarks>
/// <para>
/// Both paths are relative to the catalog root, the way the catalog report's path is, so the result describes
/// the artifact and not the machine that wrote it. The application turns them into absolute paths for its own
/// result, which is the only place that knows where the catalog folder is.
/// </para>
/// <para>
/// The sizes are this pass's own measurements and are deliberately not part of the catalog's three byte
/// measures: the scripts describe the catalog rather than belong to it, so a folder's size is the same with
/// them and without them (FR9, D10).
/// </para>
/// </remarks>
public sealed record ScriptResult
{
    /// <summary>Gets the schema file's path relative to the catalog root, or <see langword="null"/> when none was written.</summary>
    public string? SchemaPath { get; init; }

    /// <summary>Gets the schema file's size in bytes.</summary>
    public long SchemaBytes { get; init; }

    /// <summary>Gets how many executable statements the schema file holds.</summary>
    public int SchemaStatements { get; init; }

    /// <summary>
    /// Gets the data file's path relative to the catalog root, or <see langword="null"/> when the catalog holds
    /// no rows or none was written.
    /// </summary>
    public string? DataPath { get; init; }

    /// <summary>Gets the data file's size in bytes.</summary>
    public long DataBytes { get; init; }

    /// <summary>Gets how many executable statements the data file holds.</summary>
    public int DataStatements { get; init; }

    /// <summary>Gets the rows the data file writes, counted across every table.</summary>
    public long Rows { get; init; }

    /// <summary>
    /// Gets the bytes of binary payloads written into the data file as base64 — the reason a data file can be
    /// larger than the data it describes (D5).
    /// </summary>
    public long PayloadBytes { get; init; }

    /// <summary>Gets why nothing was written, or <see langword="null"/> when the pass wrote what it could.</summary>
    public ScriptRefusal? Refusal { get; init; }

    /// <summary>Gets whether the pass stopped because the caller cancelled it, which records no issue.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Gets whether the pass wrote at least the schema file.</summary>
    public bool Succeeded => Refusal is null && !Cancelled && SchemaPath is not null;
}
