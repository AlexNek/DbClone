using DbAdapter.Definitions;

namespace DatabaseInterchange.Script;

/// <summary>
/// What a script pass produced: the schema file, the data files of the family it chose, what they hold, and why
/// nothing was written when nothing was (D8, D9).
/// </summary>
/// <remarks>
/// <para>
/// Every path is relative to the catalog root, the way the catalog report's path is, so the result describes
/// the artifact and not the machine that wrote it. The application turns them into absolute paths for its own
/// result, which is the only place that knows where the catalog folder is.
/// </para>
/// <para>
/// The sizes are this pass's own measurements and are deliberately not part of the catalog's three byte
/// measures: the scripts describe the catalog rather than belong to it, so a folder's size is the same with
/// them and without them (FR9, D10).
/// </para>
/// <para>
/// The plan facts — <see cref="Profile"/>, <see cref="BudgetBytes"/>, <see cref="LargestTable"/> — describe the
/// catalog rather than the output, so the report may state them even when the writer spilled past its plan
/// (D6). The files themselves are described by <see cref="DataFiles"/>, never by the plan.
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
    /// Gets the data files the pass wrote, in the order they run: one entry for the single <c>data.sql</c>, or
    /// one per numbered file of a family. Empty when the catalog holds no rows or none was written.
    /// </summary>
    public IReadOnlyList<ScriptDataFile> DataFiles { get; init; } = [];

    /// <summary>
    /// Gets the <c>\ir</c> driver's path relative to the catalog root, or <see langword="null"/> when the pass
    /// wrote no driver — a family of one file needs none (FR10).
    /// </summary>
    public string? LoadPath { get; init; }

    /// <summary>Gets the shape the catalog's size distribution produced, before anything was written.</summary>
    public EScriptProfile Profile { get; init; }

    /// <summary>Gets the per-file budget the layout was planned with, in bytes; <c>0</c> means one file.</summary>
    public long BudgetBytes { get; init; }

    /// <summary>Gets the largest table's qualified name, or <see langword="null"/> when no table holds rows.</summary>
    public TableRef? LargestTable { get; init; }

    /// <summary>Gets the bytes the largest table was measured at, which is the number the plan reasoned with.</summary>
    public long LargestTableBytes { get; init; }

    /// <summary>Gets the rows the data files insert, counted across every table.</summary>
    public long Rows { get; init; }

    /// <summary>
    /// Gets the bytes of binary payloads written into the data files as base64 — the reason a data file can be
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
