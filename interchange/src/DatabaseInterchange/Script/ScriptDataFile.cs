namespace DatabaseInterchange.Script;

/// <summary>
/// One data file a script pass wrote — the single <c>data.sql</c> or one numbered file of a family — and what
/// it holds (D9).
/// </summary>
/// <remarks>
/// The path is relative to the catalog root, the way the schema file's is, so the result describes the
/// artifact and not the machine that wrote it. <see cref="Rows"/> is the sum of the batch row counts the
/// renderer reported as the statements were written, so it is what the file holds rather than what the plan
/// expected.
/// </remarks>
/// <param name="RelativePath">The file's path relative to the catalog root.</param>
/// <param name="Bytes">The file's size in bytes, as written.</param>
/// <param name="Statements">How many executable statements the file holds.</param>
/// <param name="Rows">How many rows those statements insert, when they are run for the first time.</param>
/// <param name="IsPartOf">
/// The qualified name of the table whose parts this file opens or continues, when the file belongs to one
/// split table; <see langword="null"/> when the file holds whole tables.
/// </param>
public sealed record ScriptDataFile(
    string RelativePath,
    long Bytes,
    int Statements,
    long Rows,
    string? IsPartOf);
