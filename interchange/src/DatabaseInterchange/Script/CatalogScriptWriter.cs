using System.Runtime.CompilerServices;
using System.Security;
using System.Text;

using CanonicalModel.Interchange;
using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;
using DatabaseInterchange.Format;
using DatabaseInterchange.Report;
using DatabaseInterchange.Transform;

namespace DatabaseInterchange.Script;

/// <summary>
/// Writes the catalog as the source engine's own SQL: <c>sql/schema.sql</c> and <c>sql/data.sql</c>, both
/// re-runnable (D2, D8).
/// </summary>
/// <remarks>
/// <para>
/// This is a post-catalog pass, not a second source read: it opens the catalog that was just closed and
/// describes what is on disk. The source connection is already gone, so nothing here can be tricked into
/// describing a database that has moved on (FR14).
/// </para>
/// <para>
/// The split of work is D1's: every statement's text comes from the adapter's
/// <see cref="ISqlScriptRenderer"/>; the layout, the file names, the order, the temp-and-move write and the
/// accounting are this class's. No SQL is written here, and no catalog path is known to the plugin.
/// </para>
/// <para>
/// Nothing this class does may fail the store. A refusal — another engine's dialect, a catalog in the direct
/// format, an object the restore itself refuses, a payload that will not open — is one warning issue and no
/// file; a write that fails is one warning issue and no retry; a cancel is silence (FR8, D11, D12).
/// </para>
/// </remarks>
internal sealed class CatalogScriptWriter
{
    /// <summary>Gets the folder the scripts live in, inside the catalog root (FR1).</summary>
    internal const string DirectoryName = "sql";

    /// <summary>Gets the schema file's name.</summary>
    internal const string SchemaFileName = "schema.sql";

    /// <summary>Gets the data file's name.</summary>
    internal const string DataFileName = "data.sql";

    /// <summary>Gets the schema file's path relative to the catalog root.</summary>
    internal const string SchemaRelativePath = DirectoryName + "/" + SchemaFileName;

    /// <summary>Gets the data file's path relative to the catalog root.</summary>
    internal const string DataRelativePath = DirectoryName + "/" + DataFileName;

    /// <summary>Gets the issue code a refused pass is recorded under (FR8).</summary>
    internal const string RefusalCode = "SqlScriptsNotWritten";

    /// <summary>Gets the issue code a failed write is recorded under (FR8).</summary>
    internal const string WriteFailureCode = "SqlScriptsWriteFailed";

    /// <summary>Gets the scratch suffix the content is written under before the move (D12).</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>Gets the encoding of the written text: UTF-8 without BOM (FR11).</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Gets the only line break the files contain, whatever the platform (FR11).</summary>
    private const string LineBreak = "\n";

    /// <summary>
    /// Writes both files for a finished catalog and states what it wrote.
    /// </summary>
    /// <param name="catalogDirectory">The catalog root this pass describes.</param>
    /// <param name="renderer">The adapter's statement text, in the source dialect (D7).</param>
    /// <param name="cycles">
    /// The foreign-key cycles the catalog's relationships hold. A cycle is named in <c>data.sql</c> because
    /// the file cannot load one: each table is its own transaction, so the first block of a cycle needs rows
    /// a later block has not committed (D6).
    /// </param>
    /// <param name="issues">
    /// The export's issue list, appended with one warning when the pass refused or failed. A cancellation adds
    /// nothing: nothing malfunctioned and the catalog is complete either way (FR8).
    /// </param>
    /// <param name="ct">Cancellation token, honoured before the first statement and between tables (FR12).</param>
    /// <returns>What was written, or why nothing was.</returns>
    internal async Task<ScriptResult> WriteAsync(
        string catalogDirectory,
        ISqlScriptRenderer renderer,
        IReadOnlyList<RelationMap.CyclicGroup> cycles,
        IList<InterchangeIssue> issues,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(catalogDirectory);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(issues);

        var directory = Path.Combine(catalogDirectory, DirectoryName);

        try
        {
            ct.ThrowIfCancellationRequested();

            await using var reader = await CatalogReader.OpenAsync(catalogDirectory, ct);

            // The two catalog-level refusals come first, so a catalog this installation cannot speak for
            // never reaches an object and never creates the folder.
            if (DescribeRefusal(reader, renderer) is { } refusal)
            {
                return Refuse(refusal, issues);
            }

            var catalog = await ReadCatalogAsync(reader, catalogDirectory, ct);

            var nativeStages = SchemaStagePlan.PlaceNativeObjects(
                catalog.NativeObjects,
                _ => true,
                out var unplaceable);

            if (unplaceable.Count > 0)
            {
                return Refuse(
                    new ScriptRefusal
                    {
                        Code = RefusalCode,
                        Reason = $"The catalog holds {unplaceable.Count} object(s) no stage of the format can "
                            + $"place: {string.Join(", ", unplaceable.Select(n => $"{n.Kind} \"{n.Name}\""))}. "
                            + "Nothing was written: a partial schema script is worse than none.",
                    },
                    issues);
            }

            var steps = SchemaStagePlan.Build(
                catalog.Schemas,
                catalog.Sequences,
                catalog.Tables,
                catalog.Routines,
                catalog.Views,
                nativeStages);

            return await WriteFilesAsync(directory, reader, renderer, steps, catalog, cycles, issues, ct);
        }
        catch (OperationCanceledException)
        {
            // A cancel stops the pass without costing the scripts of an earlier store anything: both files
            // are built under their scratch names and only moved when they are complete, so no target name
            // was touched yet (D12).
            DiscardScratch(directory);
            return new ScriptResult { Cancelled = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            DiscardScratch(directory);
            return WriteFailure(directory, ex, issues);
        }
    }

    /// <summary>
    /// Renders the schema first — a refusal there must leave no file at all — then writes both files, the tail
    /// into whichever of them the store wrote last (D2).
    /// </summary>
    private async Task<ScriptResult> WriteFilesAsync(
        string directory,
        CatalogReader reader,
        ISqlScriptRenderer renderer,
        IReadOnlyList<SchemaStageStep> steps,
        CatalogContent catalog,
        IReadOnlyList<RelationMap.CyclicGroup> cycles,
        IList<InterchangeIssue> issues,
        CancellationToken ct)
    {
        var schemaStatements = new List<string>();

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryRenderStage(renderer, step.Stage, step.Objects, out var rendered, out var refusal))
            {
                return Refuse(refusal!, issues);
            }

            schemaStatements.AddRange(rendered);
        }

        if (!TryRenderTail(renderer, catalog, out var tail, out var tailRefusal))
        {
            return Refuse(tailRefusal!, issues);
        }

        Directory.CreateDirectory(directory);

        // Both files are built under their scratch names and moved onto the target names only when both are
        // complete, so a cancel or a failure while writing costs the scripts of an earlier store nothing:
        // no target name has been touched yet (D12).
        await using var schema = new ScriptFile(directory, SchemaFileName);

        WriteSchemaPreamble(schema, renderer, catalog.HoldsData);
        schema.Write(renderer.HeaderStatements());
        schema.Write(schemaStatements);

        if (!catalog.HoldsData)
        {
            // The tail belongs to the last file the store writes: with no data file following, the schema
            // file is that file (D2).
            schema.Write(tail);

            return await PublishAsync(schema, data: null, new DataTally(), issues, directory);
        }

        await using var data = new ScriptFile(directory, DataFileName);

        WriteDataPreamble(data, renderer);
        data.Write(renderer.HeaderStatements());
        WriteCycleNote(data, cycles);

        var tally = new DataTally();

        if (await WriteBlocksAsync(data, reader, renderer, catalog, tail, tally, ct) is { } blocksRefusal)
        {
            // The scratch streams are closed before the folder is cleaned: a file held open by this process
            // is one the discard cannot remove on Windows.
            await data.DisposeAsync();
            await schema.DisposeAsync();
            DiscardScratch(directory);

            return Refuse(blocksRefusal, issues);
        }

        return await PublishAsync(schema, data, tally, issues, directory);
    }

    /// <summary>
    /// Writes one block per table, in the order the rows' own keys demand, then the tail that makes the keys
    /// true and the materialized views match (D4, D6, FR9).
    /// </summary>
    /// <returns>The refusal that stopped the pass, or <see langword="null"/> when every block was written.</returns>
    private static async Task<ScriptRefusal?> WriteBlocksAsync(
        ScriptFile file,
        CatalogReader reader,
        ISqlScriptRenderer renderer,
        CatalogContent catalog,
        IReadOnlyList<string> tail,
        DataTally tally,
        CancellationToken ct)
    {
        // A parent's rows are written before the rows that reference them: a key is enforced on every insert
        // the moment the constraint exists, so the file can only load if the table it points at already
        // holds its rows (D6). A cycle cannot be loaded either way, and its members keep the catalog's order.
        foreach (var table in TableLoadOrder.Order(catalog.Tables))
        {
            ct.ThrowIfCancellationRequested();

            var schemaName = table.SchemaRef.ObjectName();
            if (string.IsNullOrWhiteSpace(schemaName))
            {
                return new ScriptRefusal
                {
                    Code = RefusalCode,
                    Reason = $"The catalog does not name the schema the table \"{table.Name}\" lives in, "
                        + "so its rows cannot be addressed. Nothing was written about it: an omission "
                        + "reported as complete is worse than a script that is not produced.",
                };
            }

            // A table the catalog holds no rows for needs no block: the schema file already created it, and
            // an empty BEGIN/COMMIT would only pad the file. The count is the catalog's own, which is what
            // lets the rows stream rather than be held (FR9).
            var recorded = await reader.ReadTableRowCountAsync(schemaName, table.Name, ct);
            if (recorded is null or 0)
            {
                continue;
            }

            try
            {
                var rows = TallyAsync(
                    reader.ReadRowsAsync(schemaName, table.Name, ct),
                    table,
                    tally,
                    ct);

                await foreach (var statement in renderer
                                   .InsertStatements(table, rows, recorded.Value, ct)
                                   .WithCancellation(ct))
                {
                    file.Write(statement);
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
            {
                return new ScriptRefusal
                {
                    Code = RefusalCode,
                    Reason = $"The rows of the table \"{table.Name}\" could not be written: {ex.Message} "
                        + "Nothing was written: a data file that silently left a table out would report a "
                        + "database it does not describe.",
                };
            }
        }

        // This is the last file the store wrote, so the tail that makes the keys true and the materialized
        // views match belongs here (D2, D6).
        file.Write(tail);

        return null;
    }

    /// <summary>
    /// Counts a table's rows and the payload bytes it carries out of band as they pass to the renderer,
    /// because neither is known in advance once the rows stream instead of being held (FR9).
    /// </summary>
    /// <remarks>
    /// A generated column is not part of the script's column list — the server computes it — so its value is
    /// not inlined and not counted here. The row array is positional over the table's own column order, which
    /// is the order the writer resolves a value against.
    /// </remarks>
    private static async IAsyncEnumerable<DataValue[]> TallyAsync(
        IAsyncEnumerable<DataValue[]> rows,
        TableDefinition table,
        DataTally tally,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var columns = table.Columns.OrderBy(column => column.Ordinal).ToList();

        await foreach (var row in rows.WithCancellation(ct))
        {
            tally.Rows++;

            for (var i = 0; i < columns.Count && i < row.Length; i++)
            {
                if (columns[i].Generated is not null)
                {
                    continue;
                }

                if (row[i].Binary is { } payload)
                {
                    tally.PayloadBytes += payload.Length;
                }
            }

            yield return row;
        }
    }

    /// <summary>
    /// Closes both scratch files and moves them onto the target names, the schema first (D12).
    /// </summary>
    /// <remarks>
    /// A reader sees one pass's complete text or the earlier pass's complete text, never a half-written file.
    /// The one state the moves cannot undo is a schema replaced while the data file's move is refused; that
    /// data file belongs to the store before it and cannot stay beside a schema it does not describe, so it is
    /// removed and the warning names what happened.
    /// </remarks>
    private static async Task<ScriptResult> PublishAsync(
        ScriptFile schema,
        ScriptFile? data,
        DataTally tally,
        IList<InterchangeIssue> issues,
        string directory)
    {
        var schemaBytes = await schema.CloseAsync();
        var dataBytes = data is null ? 0 : await data.CloseAsync();

        try
        {
            schema.MoveToTarget();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Nothing was replaced: both target names still hold what the earlier store wrote (D12).
            DiscardScratch(directory);
            return WriteFailure(directory, ex, issues);
        }

        if (data is not null)
        {
            try
            {
                data.MoveToTarget();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                DeleteFile(Path.Combine(directory, DataFileName));
                DiscardScratch(directory);
                return WriteFailure(directory, ex, issues);
            }
        }

        return new ScriptResult
        {
            SchemaPath = SchemaRelativePath,
            SchemaBytes = schemaBytes,
            SchemaStatements = schema.Statements,
            DataPath = data is null ? null : DataRelativePath,
            DataBytes = dataBytes,
            DataStatements = data?.Statements ?? 0,
            Rows = tally.Rows,
            PayloadBytes = tally.PayloadBytes,
        };
    }

    /// <summary>
    /// States what <c>schema.sql</c> is and what a reader may rely on, including which file validates the keys
    /// (D2, FR2, FR6).
    /// </summary>
    private static void WriteSchemaPreamble(ScriptFile file, ISqlScriptRenderer renderer, bool holdsData)
    {
        var lastFile = holdsData ? DataFileName : SchemaFileName;

        file.Comment(
            "-- DbClone interchange catalog: the schema, as the captured engine wrote it.",
            $"-- Dialect: {renderer.Dialect}",
            "--",
            "-- Every statement in this file can be executed again: a second run creates no object,",
            "-- drops nothing that holds data and overwrites nothing.",
            "--",
            "-- A table that already exists is left as it is: no column, key or check is reconciled.",
            "-- A view whose column list changed fails with the server's own message naming it.",
            $"-- Foreign keys are added NOT VALID and validated at the end of {lastFile}.",
            "-- No role, grant, policy or ownership is written.",
            "-- An extension is not written: the catalog records one but no stage creates it.",
            "-- A sequence keeps the position its own declaration gives it: a catalog records no",
            "-- current value.");
        file.Comment(string.Empty);
    }

    /// <summary>States what <c>data.sql</c> is and what a reader may rely on (D2, FR2, FR6).</summary>
    private static void WriteDataPreamble(ScriptFile file, ISqlScriptRenderer renderer)
    {
        file.Comment(
            "-- DbClone interchange catalog: the rows, as the captured engine wrote them.",
            $"-- Dialect: {renderer.Dialect}",
            "--",
            "-- Run schema.sql first. Every statement in this file can be executed again: a row that is",
            "-- already on the target is skipped, never overwritten and never duplicated.",
            "-- Each table is its own transaction, so a failure leaves every earlier table complete.",
            "-- Foreign keys were added NOT VALID and are validated at the end of this file.");
        file.Comment(string.Empty);
    }

    /// <summary>
    /// States the cycles the catalog holds, because these files cannot load one and a reader deserves to know
    /// before running them rather than from a key violation (D6).
    /// </summary>
    private static void WriteCycleNote(ScriptFile file, IReadOnlyList<RelationMap.CyclicGroup> cycles)
    {
        var numbered = cycles.OrderBy(cycle => cycle.Number).ToList();
        if (numbered.Count == 0)
        {
            return;
        }

        file.Comment(
            string.Empty,
            $"-- Note: the tables below form {numbered.Count} foreign-key cycle(s), listed here. These",
            "-- files cannot load a cycle, whether its keys were declared DEFERRABLE or not: each table",
            "-- block is its own transaction, so the first block of a cycle needs rows a later block has",
            "-- not committed. The validation at the end of the file fails if the rows truly break a key.");

        foreach (var cycle in numbered)
        {
            file.Comment(
                $"--   Cycle {cycle.Number}, {cycle.Members.Count} table(s): {string.Join(", ", cycle.Members)}");
        }

        file.Comment(string.Empty);
    }

    /// <summary>
    /// Asks the renderer for the tail, and turns its refusal into one — a refresh or a validation the engine
    /// cannot express is still a reason not to write a half script.
    /// </summary>
    private static bool TryRenderTail(
        ISqlScriptRenderer renderer,
        CatalogContent catalog,
        out IReadOnlyList<string> statements,
        out ScriptRefusal? refusal)
    {
        try
        {
            statements = renderer.TailStatements(catalog.Tables, catalog.Views);
            refusal = null;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            statements = [];
            refusal = new ScriptRefusal
            {
                Code = RefusalCode,
                Reason = "The statements that finish the schema could not be written: " + ex.Message
                    + " Nothing was written: a partial schema script is worse than none.",
            };
            return false;
        }
    }

    /// <summary>
    /// Renders one stage, and on refusal renders its objects one at a time so the reason names the object the
    /// engine refused rather than the stage it was in (D11).
    /// </summary>
    private static bool TryRenderStage(
        ISqlScriptRenderer renderer,
        SchemaStage stage,
        IReadOnlyList<ISchemaObject> objects,
        out IReadOnlyList<string> statements,
        out ScriptRefusal? refusal)
    {
        try
        {
            statements = renderer.SchemaStatements(stage, objects);
            refusal = null;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            statements = [];
            refusal = new ScriptRefusal { Code = RefusalCode, Reason = DescribeStageRefusal(renderer, stage, objects, ex) };
            return false;
        }
    }

    /// <summary>
    /// Names the object of a refused stage, by handing the stage one object at a time. Rendering is pure text,
    /// so the extra calls cost nothing but the answer, and the first object that still throws is the one to
    /// blame.
    /// </summary>
    private static string DescribeStageRefusal(
        ISqlScriptRenderer renderer,
        SchemaStage stage,
        IReadOnlyList<ISchemaObject> objects,
        Exception failure)
    {
        foreach (var candidate in objects)
        {
            try
            {
                _ = renderer.SchemaStatements(stage, [candidate]);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                return $"The catalog holds an object the {Screen(stage)} stage cannot script — "
                    + $"{candidate.Kind} \"{candidate.Name}\": {ex.Message} Nothing was written: a partial "
                    + "schema script is worse than none.";
            }
        }

        return $"The {Screen(stage)} statements could not be written: {failure.Message} Nothing was written: "
            + "a partial schema script is worse than none.";
    }

    /// <summary>
    /// States the two catalog-level refusals: an engine this installation does not speak, and a catalog whose
    /// model carries no native text to speak with (D11, FR4).
    /// </summary>
    private static ScriptRefusal? DescribeRefusal(CatalogReader reader, ISqlScriptRenderer renderer)
    {
        var sourceDialect = reader.SourceDialect;

        if (string.IsNullOrWhiteSpace(sourceDialect))
        {
            return new ScriptRefusal
            {
                Code = RefusalCode,
                Reason = "The catalog records no source dialect, so there is no engine for the script to be "
                    + "written in. Nothing was written.",
            };
        }

        if (!string.Equals(sourceDialect, renderer.Dialect, StringComparison.OrdinalIgnoreCase))
        {
            return new ScriptRefusal
            {
                Code = RefusalCode,
                Reason = $"The catalog was captured from \"{sourceDialect}\" and this installation writes "
                    + $"\"{renderer.Dialect}\". A script is the source engine's own SQL and is never "
                    + "translated, so nothing was written.",
            };
        }

        if (reader.IsDirectFormat)
        {
            return new ScriptRefusal
            {
                Code = RefusalCode,
                Reason = "The catalog records the source model directly rather than the interchange sections, "
                    + "so it holds no captured index, routine or view text to write. Nothing was written.",
            };
        }

        return null;
    }

    /// <summary>Reads every section the pass needs, in the order a restore reads them.</summary>
    private static async Task<CatalogContent> ReadCatalogAsync(
        CatalogReader reader,
        string catalogDirectory,
        CancellationToken ct)
    {
        var schemas = await reader.ReadSectionAsync<SchemaDefinition>("schemas", ct);
        var sequences = await reader.ReadSectionAsync<SequenceDefinition>("sequences", ct);
        var tables = await reader.ReadSectionAsync<TableDefinition>("tables", ct);
        var routines = await reader.ReadSectionAsync<RoutineDefinition>("routines", ct);
        var views = await reader.ReadSectionAsync<ViewDefinition>("views", ct);

        var nativeObjects = await new NativeObjectReader().ReadNativeObjectsAsync(
            catalogDirectory,
            reader.SourceDialect,
            ct);

        // The manifest's own count is the answer to "does this catalog hold data": a schema-only store
        // records zero rows and has no data file to write, so the tail that finishes the schema belongs in
        // schema.sql instead (D2).
        return new CatalogContent(
            schemas,
            sequences,
            tables,
            routines,
            views,
            nativeObjects,
            reader.Manifest.Counts.Rows > 0);
    }

    /// <summary>Records a refusal as one warning and answers with the result that carries it.</summary>
    private static ScriptResult Refuse(ScriptRefusal refusal, IList<InterchangeIssue> issues)
    {
        issues.Add(new InterchangeIssue(refusal.Code, EIssueSeverity.Warning, refusal.Reason));
        return new ScriptResult { Refusal = refusal };
    }

    /// <summary>
    /// Records a write that the filesystem refused as one warning and answers with the result that states it.
    /// </summary>
    private static ScriptResult WriteFailure(
        string directory,
        Exception failure,
        IList<InterchangeIssue> issues)
    {
        var reason = $"The SQL scripts could not be written into {directory}: {failure.Message}";

        issues.Add(new InterchangeIssue(WriteFailureCode, EIssueSeverity.Warning, reason));

        return new ScriptResult
        {
            Refusal = new ScriptRefusal { Code = WriteFailureCode, Reason = reason },
        };
    }

    /// <summary>
    /// Removes the scratch files this pass opened, and then the folder when nothing else is in it — the target
    /// names are left exactly as they were found, which is what makes a cancelled or failed pass cost the
    /// scripts of an earlier store nothing (D12).
    /// </summary>
    private static void DiscardScratch(string directory)
    {
        DeleteFile(Path.Combine(directory, SchemaFileName) + TempSuffix);
        DeleteFile(Path.Combine(directory, DataFileName) + TempSuffix);

        try
        {
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
            // A folder that will not go is inert: nothing reads an empty sql/ folder.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>Deletes one file, treating a filesystem that will not as nothing to report.</summary>
    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The next store overwrites the name anyway.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>Spells a stage the way a refusal reads.</summary>
    private static string Screen(SchemaStage stage) => stage.ToString().ToLowerInvariant();

    /// <summary>
    /// One script file, written content-then-move: the text lands in <c>&lt;name&gt;.tmp</c>, that stream is
    /// closed, and only then does the move replace the target inside the same folder (D12).
    /// </summary>
    /// <remarks>
    /// The target is never opened for writing, so no failure — a full disk, a killed process, an exception
    /// between flush and move — can leave a truncated script at the name a user would run. A reader sees
    /// either the previous complete text or the new complete text.
    /// </remarks>
    private sealed class ScriptFile : IAsyncDisposable
    {
        private readonly FileStream _stream;
        private readonly StreamWriter _writer;
        private readonly string _targetPath;
        private readonly string _tempPath;

        /// <summary>Initializes a new instance of the <see cref="ScriptFile"/> class.</summary>
        /// <param name="directory">The folder the file belongs to.</param>
        /// <param name="fileName">The file's name inside that folder.</param>
        internal ScriptFile(string directory, string fileName)
        {
            _targetPath = Path.Combine(directory, fileName);
            _tempPath = _targetPath + TempSuffix;
            _stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new StreamWriter(_stream, Utf8NoBom) { NewLine = LineBreak };
        }

        /// <summary>Gets how many executable statements have been written.</summary>
        internal int Statements { get; private set; }

        /// <summary>Writes lines that are comments or blank, which no statement count includes.</summary>
        internal void Comment(params string[] lines)
        {
            foreach (var line in lines)
            {
                _writer.WriteLine(line);
            }
        }

        /// <summary>Writes statements, each followed by one blank line.</summary>
        internal void Write(IEnumerable<string> statements)
        {
            foreach (var statement in statements)
            {
                Write(statement);
            }
        }

        /// <summary>Writes one statement and counts it, unless it is a comment.</summary>
        internal void Write(string statement)
        {
            _writer.WriteLine(statement);
            _writer.WriteLine();

            if (!statement.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                Statements++;
            }
        }

        /// <summary>Closes the content, leaving it at its scratch name.</summary>
        /// <returns>The number of bytes written.</returns>
        internal async Task<long> CloseAsync()
        {
            await _writer.FlushAsync();
            var bytes = _stream.Position;

            await _writer.DisposeAsync();

            return bytes;
        }

        /// <summary>Moves the closed content onto the target name, replacing what is there (D12).</summary>
        internal void MoveToTarget() => File.Move(_tempPath, _targetPath, overwrite: true);

        /// <inheritdoc />
        public async ValueTask DisposeAsync() => await _writer.DisposeAsync();
    }

    /// <summary>
    /// What the data pass counted as it wrote. The rows stream to the renderer instead of being held, so the
    /// totals are gathered on the way past rather than measured afterwards (FR9).
    /// </summary>
    private sealed class DataTally
    {
        /// <summary>Gets or sets how many rows were handed to the renderer.</summary>
        internal long Rows { get; set; }

        /// <summary>Gets or sets the bytes of the payloads those rows carried out of band.</summary>
        internal long PayloadBytes { get; set; }
    }

    /// <summary>What the pass reads out of the catalog, before any text is produced.</summary>
    private sealed record CatalogContent(
        IReadOnlyList<SchemaDefinition> Schemas,
        IReadOnlyList<SequenceDefinition> Sequences,
        IReadOnlyList<TableDefinition> Tables,
        IReadOnlyList<RoutineDefinition> Routines,
        IReadOnlyList<ViewDefinition> Views,
        IReadOnlyList<NativeObjectDefinition> NativeObjects,
        bool HoldsData);
}
