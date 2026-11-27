using System.Security;
using System.Text;
using CanonicalModel.Interchange;
using DatabaseInterchange.Script;

namespace DatabaseInterchange.Report;

/// <summary>
/// Renders the catalog summary and lands it in the catalog root (D4, D4.1).
/// </summary>
/// <remarks>
/// <para>
/// This is the feature's only <c>System.IO</c> call site, which is what keeps the rest of the
/// <c>Report/</c> folder testable against an in-memory snapshot: every other class there turns one model
/// into another model or into a string, and never touches a disk.
/// </para>
/// <para>
/// The write is content-then-move. The rendered text goes to <c>summary.md.tmp</c>, that stream is
/// flushed and closed, and only then does <c>File.Move</c> replace the target inside the same directory.
/// Three consequences are load-bearing, and §7 asserts each of them:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>summary.md</c> is never opened for writing, so no failure — disk full, killed process, an exception
/// between flush and move — can leave a truncated report at that path. A reader sees either the previous
/// complete text or the new complete text.</description></item>
/// <item><description>
/// A failed move cannot destroy a report: nothing is retried, deleted or written to the target directly,
/// so whatever text was there survives byte for byte and the store continues as successful-with-issues
/// (FR13).</description></item>
/// <item><description>
/// Temp cleanup is best-effort. A filesystem that refuses to remove its own scratch file is not evidence
/// that the store failed; an orphan <c>.tmp</c> is as inert to Restore as the file it was making
/// (FR14, D8).</description></item>
/// </list>
/// <para>
/// Cancellation is answered by D4.2: the token is honoured before the render and again immediately
/// before the write-and-move pair, and never between the two halves of that pair. A cancel in this stage
/// stops the report and records no issue — the catalog on disk is finished, and telling the user it is
/// unusable would be a lie.
/// </para>
/// </remarks>
internal sealed class CatalogReportWriter
{
    /// <summary>Gets the report's file name inside the catalog root (FR15).</summary>
    internal const string ReportFileName = "summary.md";

    /// <summary>Gets the scratch name the content is written under before the move (D4.1).</summary>
    internal const string TempFileName = "summary.md.tmp";

    /// <summary>Gets the issue code a failed report write is recorded under (FR13).</summary>
    internal const string WriteFailureCode = "CatalogReportNotWritten";

    /// <summary>Gets the encoding of the written text: UTF-8 without BOM, <c>\n</c> only (D9.2).</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly MarkdownCatalogReportRenderer _renderer = new();

    /// <summary>
    /// Renders the document and writes it into <paramref name="catalogDirectory"/>.
    /// </summary>
    /// <param name="catalogDirectory">The catalog root the report belongs to (FR15).</param>
    /// <param name="statistics">The measured account of the finished catalog.</param>
    /// <param name="map">The relation facts analysed from the same snapshot.</param>
    /// <param name="scripts">
    /// What the SQL script pass produced, or <see langword="null"/> when that option was off — the section that
    /// names the scripts is present exactly when the pass ran (FR7, D9).
    /// </param>
    /// <param name="issues">
    /// The export's issue list, appended with one warning when the write itself failed (FR13). A
    /// cancellation adds nothing: nothing malfunctioned.
    /// </param>
    /// <param name="ct">Cancellation token, honoured as a stop rather than a failure (D4.2).</param>
    /// <returns>
    /// The report's path relative to the catalog root, or <see langword="null"/> when the write failed or
    /// the stage was cancelled.
    /// </returns>
    public async Task<string?> WriteAsync(
        string catalogDirectory,
        CatalogStatistics statistics,
        RelationMap map,
        ScriptResult? scripts,
        IList<InterchangeIssue> issues,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(catalogDirectory);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(issues);

        var target = Path.Combine(catalogDirectory, ReportFileName);
        var temp = Path.Combine(catalogDirectory, TempFileName);

        try
        {
            // Both halves of D4.2 are inside the try: a cancel that arrived before the render is answered
            // the same way as one that arrived after it — the report stops, the catalog is not blamed.
            ct.ThrowIfCancellationRequested();
            var document = _renderer.Render(statistics, map, scripts);

            ct.ThrowIfCancellationRequested();
            await WriteContentAsync(temp, document, ct);
            File.Move(temp, target, overwrite: true);
            return ReportFileName;
        }
        catch (OperationCanceledException)
        {
            DeleteTemp(temp);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            DeleteTemp(temp);
            issues.Add(new InterchangeIssue(
                WriteFailureCode,
                EIssueSeverity.Warning,
                $"The catalog summary could not be written into {target}: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// Writes the rendered text to the scratch name and closes it. The stream is disposed before this
    /// returns, so the move that follows sees a complete file (D4.1).
    /// </summary>
    private static async Task WriteContentAsync(string tempPath, string document, CancellationToken ct)
    {
        var bytes = Utf8NoBom.GetBytes(document);

        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Removes the scratch file, ignoring a filesystem that will not. A lingering <c>.tmp</c> is unseen by
    /// the verifier, inert to Restore, and goes with the directory on the next store (D4.1.3).
    /// </summary>
    private static void DeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
            // Deliberately silent: the report is a courtesy, and a scratch file is not something the user
            // needs to be told about when the catalog itself is complete and restorable.
        }
        catch (UnauthorizedAccessException)
        {
            // Same — a folder that will not give up its scratch file still gives up the summary.
        }
    }
}
