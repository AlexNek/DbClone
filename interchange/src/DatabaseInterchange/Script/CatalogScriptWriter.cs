using System.Globalization;
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
/// Writes the catalog as the source engine's own SQL: <c>sql/schema.sql</c> and either <c>sql/data.sql</c> or
/// the numbered family <c>sql/data/0001.sql …</c> beside its <c>sql/load.psql</c> driver, all re-runnable
/// (D2, D7, D8).
/// </summary>
/// <remarks>
/// <para>
/// This is a post-catalog pass, not a second source read: it opens the catalog that was just closed and
/// describes what is on disk. The source connection is already gone, so nothing here can be tricked into
/// describing a database that has moved on (FR14).
/// </para>
/// <para>
/// The split of work is D1's: every statement's text comes from the adapter's
/// <see cref="ISqlScriptRenderer"/>; the layout, the file names, the order, where a table's block is cut, the
/// temp-and-move write and the accounting are this class's. No SQL is written here, and no catalog path is
/// known to the plugin.
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

    /// <summary>Gets the folder a numbered family lives in, inside the script folder (D7).</summary>
    internal const string DataDirectoryName = "data";

    /// <summary>Gets the schema file's name.</summary>
    internal const string SchemaFileName = "schema.sql";

    /// <summary>Gets the single data file's name.</summary>
    internal const string DataFileName = "data.sql";

    /// <summary>Gets the psql driver's name (FR10).</summary>
    internal const string LoadFileName = "load.psql";

    /// <summary>Gets the schema file's path relative to the catalog root.</summary>
    internal const string SchemaRelativePath = DirectoryName + "/" + SchemaFileName;

    /// <summary>Gets the single data file's path relative to the catalog root.</summary>
    internal const string DataRelativePath = DirectoryName + "/" + DataFileName;

    /// <summary>Gets the numbered family's folder relative to the catalog root.</summary>
    internal const string DataFolderRelativePath = DirectoryName + "/" + DataDirectoryName;

    /// <summary>Gets the psql driver's path relative to the catalog root.</summary>
    internal const string LoadRelativePath = DirectoryName + "/" + LoadFileName;

    /// <summary>Gets the issue code a refused pass is recorded under (FR8).</summary>
    internal const string RefusalCode = "SqlScriptsNotWritten";

    /// <summary>Gets the issue code a failed write is recorded under (FR8).</summary>
    internal const string WriteFailureCode = "SqlScriptsWriteFailed";

    /// <summary>
    /// Gets the issue code a written pass that could not remove an earlier store's file is recorded under: the
    /// scripts are this pass's, the folder carries what would not go (FR11, FR15).
    /// </summary>
    internal const string PruneWarningCode = "SqlScriptsStaleFilesLeft";

    /// <summary>Gets the narrowest an ordinal is padded to, so a small family sorts the way a large one does (D3).</summary>
    private const int MinimumOrdinalWidth = 4;

    /// <summary>Gets the scratch suffix the content is written under before the move (D12).</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>Gets the encoding of the written text: UTF-8 without BOM (FR11).</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Gets the only line break the files contain, whatever the platform (FR11).</summary>
    private const string LineBreak = "\n";

    /// <summary>
    /// Writes a finished catalog as the source engine's own SQL and states what it wrote.
    /// </summary>
    /// <param name="catalogDirectory">The catalog root this pass describes.</param>
    /// <param name="renderer">The adapter's statement text, in the source dialect (D7).</param>
    /// <param name="cycles">
    /// The foreign-key cycles the catalog's relationships hold. A cycle is named in the first data file because
    /// the files cannot load one: each block is its own transaction, so the first block of a cycle needs rows
    /// a later block has not committed (D6).
    /// </param>
    /// <param name="issues">
    /// The export's issue list, appended with one warning when the pass refused or failed. A cancellation adds
    /// nothing: nothing malfunctioned and the catalog is complete either way (FR8).
    /// </param>
    /// <param name="maxDataFileBytes">
    /// The largest a data file may become before the next one opens, in bytes; <c>0</c> writes today's single
    /// <c>data.sql</c> whatever the size (FR13).
    /// </param>
    /// <param name="ct">Cancellation token, honoured before the first statement and between tables (FR12).</param>
    /// <returns>What was written, or why nothing was.</returns>
    internal async Task<ScriptResult> WriteAsync(
        string catalogDirectory,
        ISqlScriptRenderer renderer,
        IReadOnlyList<RelationMap.CyclicGroup> cycles,
        IList<InterchangeIssue> issues,
        long maxDataFileBytes,
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
                catalog.Extensions,
                catalog.Sequences,
                catalog.Tables,
                catalog.Routines,
                catalog.Views,
                nativeStages);

            return await WriteFilesAsync(directory, reader, renderer, steps, catalog, cycles, issues, maxDataFileBytes, ct);
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
    /// Renders the schema first — a refusal there must leave no file at all — then measures the catalog, plans
    /// the data family from what it measured and writes it, the tail into the last file the pass writes
    /// (D1, D2, FR3, FR9).
    /// </summary>
    private async Task<ScriptResult> WriteFilesAsync(
        string directory,
        CatalogReader reader,
        ISqlScriptRenderer renderer,
        IReadOnlyList<SchemaStageStep> steps,
        CatalogContent catalog,
        IReadOnlyList<RelationMap.CyclicGroup> cycles,
        IList<InterchangeIssue> issues,
        long maxDataFileBytes,
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

        // The two stages that make the layout a decision rather than a consequence of writing: the catalog is
        // measured from its own records, and the family is planned from those measures before the first data
        // byte exists (FR3, D1).
        var plan = catalog.HoldsData
            ? await PlanAsync(reader, catalog, maxDataFileBytes, ct)
            : ScriptPlanner.None(maxDataFileBytes);

        Directory.CreateDirectory(directory);

        // Both files are built under their scratch names and moved onto the target names only when every file
        // is complete, so a cancel or a failure while writing costs the scripts of an earlier store nothing:
        // no target name has been touched yet (D12).
        await using var schema = new ScriptFile(directory, SchemaFileName);

        // The shape is fixed here, before the first data byte: the schema preamble names the file that
        // validates the keys from what the plan decided, which is why a spill can never contradict it (D4).
        WriteSchemaPreamble(schema, renderer, catalog.HoldsData, plan.IsFamily);
        schema.Write(renderer.HeaderStatements());
        schema.Write(schemaStatements);

        if (!catalog.HoldsData)
        {
            // The tail belongs to the last file the store writes: with no data file following, the schema
            // file is that file (D2, FR2).
            schema.Write(tail);

            return await PublishAsync(
                directory,
                schema,
                [],
                new Dictionary<TableRef, int>(),
                plan,
                new DataTally(),
                issues);
        }

        return await WriteDataAsync(directory, reader, renderer, cycles, tail, plan, schema, issues, ct);
    }

    /// <summary>Measures every table the pass writes and plans the family from those measures (FR3, D1).</summary>
    private static async Task<ScriptPlan> PlanAsync(
        CatalogReader reader,
        CatalogContent catalog,
        long maxDataFileBytes,
        CancellationToken ct)
    {
        // A parent's rows must be written before the rows that reference them, so the order the plan packs in
        // is the order the blocks run in (FR7).
        var footprints = await reader.ReadTableFootprintsAsync(TableLoadOrder.Order(catalog.Tables), ct);

        return ScriptPlanner.Plan(footprints, maxDataFileBytes);
    }

    /// <summary>
    /// Writes the data family the plan chose: one file per planned file, and a new one wherever the next
    /// statement no longer fits, the block that was cut closed and reopened around the boundary (D3, D6, FR5).
    /// </summary>
    private static async Task<ScriptResult> WriteDataAsync(
        string directory,
        CatalogReader reader,
        ISqlScriptRenderer renderer,
        IReadOnlyList<RelationMap.CyclicGroup> cycles,
        IReadOnlyList<string> tail,
        ScriptPlan plan,
        ScriptFile schema,
        IList<InterchangeIssue> issues,
        CancellationToken ct)
    {
        var tally = new DataTally();

        // The tail is rendered before the data pass, so its size is known while the files are still being
        // filled: those bytes are reserved in every cut, which is what keeps the statement that validates the
        // keys inside the budget instead of pushing the last file past it (D6, FR9).
        var tailBytes = tail.Sum(statement => Utf8NoBom.GetByteCount(statement) + (2 * LineBreak.Length));
        var data = new DataWriter(directory, reader, renderer, plan, tally, tailBytes);

        await using (data)
        {
            data.OpenFirst();

            // The preamble, the header and the cycle note belong to the first data file: a continuation file
            // starts with the block it continues (D2, D4).
            WriteDataPreamble(data.Current, renderer, plan.IsFamily);
            data.Current.Write(renderer.HeaderStatements());
            WriteCycleNote(data.Current, cycles, plan.IsFamily);

            var written = new HashSet<TableRef>();
            var first = true;

            foreach (var planned in plan.Files)
            {
                ct.ThrowIfCancellationRequested();

                var tables = Planned(planned, written);
                if (tables.Count == 0)
                {
                    // A giant's later planned parts hold no table of their own: the writer cut that table's
                    // block while it was writing it, so nothing is opened for them (D3, D6).
                    continue;
                }

                if (first)
                {
                    first = false;
                }
                else
                {
                    await data.StartNextFileAsync();
                }

                foreach (var table in tables)
                {
                    written.Add(Reference(table));

                    if (await data.WriteTableAsync(table, ct) is { } blocksRefusal)
                    {
                        // The scratch streams are closed before the folder is cleaned: a file held open by
                        // this process is one the discard cannot remove on Windows.
                        await data.DisposeAsync();
                        await schema.DisposeAsync();
                        DiscardScratch(directory);

                        return Refuse(blocksRefusal, issues);
                    }
                }
            }

            // This is the last file the store wrote, so the tail that makes the keys true and the materialized
            // views match belongs here (D2, D6, FR9). The room for it was reserved while the file was filled.
            data.Current.Write(tail);

            await data.CompleteAsync();

            return await PublishAsync(directory, schema, data.Files, data.Parts, plan, tally, issues);
        }
    }

    /// <summary>The tables a planned file holds that no earlier file wrote, in the order they run (FR7).</summary>
    private static List<TableDefinition> Planned(ScriptPlanFile planned, HashSet<TableRef> written)
    {
        var tables = new List<TableDefinition>();
        var seen = new HashSet<TableRef>();

        foreach (var chunk in planned.Chunks)
        {
            if (written.Contains(Reference(chunk.Table)) || !seen.Add(Reference(chunk.Table)))
            {
                continue;
            }

            tables.Add(chunk.Table);
        }

        return tables;
    }

    /// <summary>How the plan and the result name one table.</summary>
    private static TableRef Reference(TableDefinition table) =>
        new(table.SchemaRef.ObjectName() ?? string.Empty, table.Name);

    /// <summary>
    /// The data files of one pass: the file open now, the ones already closed, the part each table has reached
    /// and the budget that decides when the next file opens (D3, D8, FR5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A numbered family is written under ordinal scratch names, because the width of the final name is not
    /// known until the count is (D3). A pass whose plan fits one file keeps today's <c>data.sql</c> and never
    /// opens a second one, whatever the bytes turn out to be (D6).
    /// </para>
    /// <para>
    /// A cut is only ever made between two statements: the block is closed where the file ends and opened again
    /// in the next one, so a table larger than the budget becomes several complete transactions and no
    /// statement is ever split (FR5, FR6).
    /// </para>
    /// </remarks>
    private sealed class DataWriter : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _dataDirectory;
        private readonly CatalogReader _reader;
        private readonly ISqlScriptRenderer _renderer;
        private readonly ScriptPlan _plan;
        private readonly DataTally _tally;
        private readonly long _tailBytes;
        private readonly List<ClosedFile> _files = [];
        private readonly Dictionary<TableRef, int> _parts = [];
        private readonly List<TableRef> _tables = [];
        private ScriptFile? _current;
        private int _ordinal;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="DataWriter"/> class.</summary>
        /// <param name="directory">The script folder the files are written in.</param>
        /// <param name="reader">The catalog the rows are read from.</param>
        /// <param name="renderer">The adapter's statement text, in the source dialect (D1).</param>
        /// <param name="plan">The family the plan chose, which fixes its shape before anything is written.</param>
        /// <param name="tally">What the rows are counted into as they pass (FR9).</param>
        /// <param name="tailBytes">
        /// The bytes the tail will take in the last file, reserved in every cut so the file that carries it
        /// stays inside the budget (D6, FR9).
        /// </param>
        internal DataWriter(
            string directory,
            CatalogReader reader,
            ISqlScriptRenderer renderer,
            ScriptPlan plan,
            DataTally tally,
            long tailBytes)
        {
            _directory = directory;
            _dataDirectory = Path.Combine(directory, DataDirectoryName);
            _reader = reader;
            _renderer = renderer;
            _plan = plan;
            _tally = tally;

            // A tail larger than a whole file leaves nothing to reserve: the last file passes the budget
            // whatever is cut, and reserving would only reduce every file to a single statement.
            _tailBytes = plan.BudgetBytes > 0 && tailBytes < plan.BudgetBytes ? tailBytes : 0;
        }

        /// <summary>Gets the file being written, which is the first one until a boundary closes it.</summary>
        internal ScriptFile Current => _current ?? throw new InvalidOperationException("No data file is open.");

        /// <summary>Gets the files closed so far, in the order they were written.</summary>
        internal IReadOnlyList<ClosedFile> Files => _files;

        /// <summary>Gets how many blocks each table was written in, which is what tells a split table apart (D9).</summary>
        internal IReadOnlyDictionary<TableRef, int> Parts => _parts;

        /// <summary>Opens the first data file, creating the family's folder when the plan writes one (D7).</summary>
        internal void OpenFirst()
        {
            if (_plan.IsFamily)
            {
                Directory.CreateDirectory(_dataDirectory);
            }

            Open();
        }

        /// <summary>Closes the open file and opens the next one, at the plan's next file boundary (D3).</summary>
        internal async Task StartNextFileAsync()
        {
            await CloseCurrentAsync();
            Open();
        }

        /// <summary>Closes the file that is open, so publishing sees every file the pass wrote (D8).</summary>
        internal Task CompleteAsync() => CloseCurrentAsync();

        /// <summary>
        /// Writes one table's block, reopening it in the next file wherever the next statement would push the
        /// file past the budget. A statement larger than the whole budget stays alone in its file, because no
        /// statement is ever split (FR5, FR6).
        /// </summary>
        /// <returns>The refusal that stopped the pass, or <see langword="null"/> when the block was written.</returns>
        internal async Task<ScriptRefusal?> WriteTableAsync(TableDefinition table, CancellationToken ct)
        {
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
            var recorded = await _reader.ReadTableRowCountAsync(schemaName, table.Name, ct);
            if (recorded is null or 0)
            {
                return null;
            }

            var identity = Reference(table);
            var split = _plan.IsSplit(identity);
            var endBytes = 0L;
            var opened = false;

            // The opener is asked for before the block exists, so a file that cannot hold the block's first
            // statement ends before that block rather than after it (FR5, FR6). The renderer is pure text, so
            // asking twice costs nothing and keeps the byte count exact.
            IReadOnlyList<string> NextOpener()
            {
                var number = _parts.GetValueOrDefault(identity) + 1;

                // A table the plan keeps whole carries no part line until the actual bytes split it; from the
                // second block on, every block says which part it continues (D4).
                var part = number == 1 && !split
                    ? ScriptBlockPart.Whole
                    : new ScriptBlockPart(number);

                return _renderer.BeginTableBlock(table, recorded.Value, part);
            }

            void OpenBlock(IReadOnlyList<string> opener)
            {
                _parts[identity] = _parts.GetValueOrDefault(identity) + 1;

                Write(opener);

                endBytes = _renderer.EndTableBlock().Sum(BytesOf);
                opened = true;

                if (!_tables.Contains(identity))
                {
                    _tables.Add(identity);
                }
            }

            bool DoesNotFit(string statement) =>
                _plan.IsFamily
                && _plan.BudgetBytes > 0
                && Current.Bytes + BytesOf(statement) + endBytes + _tailBytes > _plan.BudgetBytes;

            // A file that already holds text and cannot hold this block as well ends here: the cut comes
            // before the opener, so no file ends with a transaction that inserts nothing and no statement is
            // ever written into a file the budget had already closed (FR5, FR6). The first file's preamble,
            // header and cycle note count as its bytes, so the envelope is inside the budget like everything
            // else; a block that alone exceeds the budget is still written, because no statement is split and
            // nothing else could be written in its place.
            bool CannotOpen(string statement)
            {
                if (!_plan.IsFamily || _plan.BudgetBytes <= 0 || Current.Bytes == 0)
                {
                    return false;
                }

                var needed = NextOpener().Sum(BytesOf)
                    + BytesOf(statement)
                    + _renderer.EndTableBlock().Sum(BytesOf)
                    + _tailBytes;

                return Current.Bytes + needed > _plan.BudgetBytes;
            }

            try
            {
                var rows = TallyAsync(_reader.ReadRowsAsync(schemaName, table.Name, ct), table, _tally, ct);
                var inserts = _renderer.InsertStatements(table, rows, ct).GetAsyncEnumerator(ct);

                await using (inserts)
                {
                    while (await inserts.MoveNextAsync())
                    {
                        var (statement, batchRows) = inserts.Current;

                        if (!opened)
                        {
                            if (CannotOpen(statement))
                            {
                                await CloseCurrentAsync();
                                Open();
                            }

                            OpenBlock(NextOpener());
                        }
                        else if (DoesNotFit(statement))
                        {
                            // The file ends where the next statement would no longer fit: the block is closed
                            // here and opened again in the next file, so every file holds complete transactions
                            // and the table becomes several of them (FR5).
                            Write(_renderer.EndTableBlock());
                            await CloseCurrentAsync();
                            Open();
                            opened = false;
                            OpenBlock(NextOpener());
                        }

                        Current.Write(statement);
                        Current.AddRows(batchRows);
                    }
                }

                if (opened)
                {
                    Write(_renderer.EndTableBlock());
                }

                return null;
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

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_current is not null)
            {
                await _current.DisposeAsync();
                _current = null;
            }
        }

        private void Open()
        {
            _ordinal++;
            _tables.Clear();

            _current = _plan.IsFamily
                ? new ScriptFile(_dataDirectory, _ordinal.ToString(CultureInfo.InvariantCulture))
                : new ScriptFile(_directory, DataFileName);
        }

        private async Task CloseCurrentAsync()
        {
            if (_current is null)
            {
                return;
            }

            await _current.CloseAsync();

            _files.Add(new ClosedFile(_ordinal, _current, _tables.ToArray()));
            _current = null;
        }

        private void Write(IReadOnlyList<string> statements)
        {
            foreach (var statement in statements)
            {
                Current.Write(statement);
            }
        }

        /// <summary>The bytes a statement takes once written: its line and the blank line after it.</summary>
        private static long BytesOf(string statement) =>
            Utf8NoBom.GetByteCount(statement) + (2 * LineBreak.Length);
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
    /// Closes the last file and moves every file onto its target name — the schema first, then the data files
    /// in order, then the driver — recording what this pass committed so a later refusal removes exactly that
    /// (D8, FR11).
    /// </summary>
    /// <remarks>
    /// A reader sees one pass's complete text or the earlier pass's complete text, never a half-written file.
    /// The one state the moves cannot undo is a file replaced while a later move is refused; the warning then
    /// names what happened and the folder holds the earlier store's files minus the names this pass replaced,
    /// never a partial one.
    /// </remarks>
    private static async Task<ScriptResult> PublishAsync(
        string directory,
        ScriptFile schema,
        IReadOnlyList<ClosedFile> dataFiles,
        IReadOnlyDictionary<TableRef, int> parts,
        ScriptPlan plan,
        DataTally tally,
        IList<InterchangeIssue> issues)
    {
        var family = plan.IsFamily;

        // The width is taken from the files this pass actually produced, so a spill past the plan's count still
        // sorts in write order (D3, D12).
        var width = Math.Max(MinimumOrdinalWidth, dataFiles.Count.ToString(CultureInfo.InvariantCulture).Length);

        var names = new List<string>(dataFiles.Count);

        foreach (var file in dataFiles)
        {
            names.Add(family
                ? file.Ordinal.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + ".sql"
                : DataFileName);
        }

        var published = new List<ScriptDataFile>(dataFiles.Count);

        for (var index = 0; index < dataFiles.Count; index++)
        {
            published.Add(new ScriptDataFile(
                family ? $"{DataFolderRelativePath}/{names[index]}" : DataRelativePath,
                dataFiles[index].File.Bytes,
                dataFiles[index].File.Statements,
                dataFiles[index].File.Rows,
                IsPartOf(dataFiles[index], parts)));
        }

        // The driver lists what exists, never what the plan expected (FR10, D6), and is written only when a
        // run of the files in name order needs a single command.
        ScriptFile? driver = null;

        if (published.Count > 1)
        {
            driver = new ScriptFile(directory, LoadFileName);
            WriteLoadDriver(driver, names.Select(name => $"{DataDirectoryName}/{name}"));
        }

        try
        {
            await schema.CloseAsync();

            if (driver is not null)
            {
                await driver.CloseAsync();
            }
        }
        catch
        {
            // The driver is released before the failure leaves this method, so the cleanup that follows can
            // remove its scratch file: one this process still holds is one Windows will not delete (D12).
            if (driver is not null)
            {
                await driver.DisposeAsync();
            }

            throw;
        }

        var committed = new List<string>();

        try
        {
            committed.Add(schema.MoveToTarget());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Nothing was replaced: every target name still holds what the earlier store wrote (D12).
            DiscardScratch(directory);
            return WriteFailure(directory, ex, issues);
        }

        for (var index = 0; index < dataFiles.Count; index++)
        {
            try
            {
                committed.Add(dataFiles[index].File.MoveToTarget(names[index]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                RemoveCommitted(committed);
                DiscardScratch(directory);
                return WriteFailure(directory, ex, issues);
            }
        }

        if (driver is not null)
        {
            try
            {
                committed.Add(driver.MoveToTarget());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                RemoveCommitted(committed);
                DiscardScratch(directory);
                return WriteFailure(directory, ex, issues);
            }
        }

        // Only once every move has landed is anything of the other shape removed: a failure above left the
        // folder holding the earlier store's files minus the names this pass replaced (D8, FR11).
        Prune(directory, published, family, driver is not null, issues);

        return new ScriptResult
        {
            SchemaPath = SchemaRelativePath,
            SchemaBytes = schema.Bytes,
            SchemaStatements = schema.Statements,
            DataFiles = published,
            LoadPath = driver is null ? null : LoadRelativePath,
            Profile = plan.Profile,
            BudgetBytes = plan.BudgetBytes,
            LargestTable = plan.LargestTable,
            LargestTableBytes = plan.LargestTableBytes,
            Rows = tally.Rows,
            PayloadBytes = tally.PayloadBytes,
        };
    }

    /// <summary>
    /// Writes the <c>psql</c> driver: a relative include per file the pass wrote, in run order, the schema
    /// first (FR10, D7).
    /// </summary>
    /// <remarks>
    /// <c>\ir</c> resolves an include against the driver's own folder, so the one command works from any
    /// directory. <c>ON_ERROR_STOP</c> is deliberately not set: what a failed block does is the reader's
    /// choice, and feature 08's cycle contract depends on the validation after it (D7).
    /// </remarks>
    private static void WriteLoadDriver(ScriptFile driver, IEnumerable<string> dataFileNames)
    {
        driver.Comment(
            "-- DbClone interchange catalog: load this catalog into an empty database with",
            "--",
            "--   psql -f sql/load.psql",
            "--",
            "-- Every include resolves against this file's own folder, and the files run in the order the",
            "-- store wrote them.",
            "--",
            "-- ON_ERROR_STOP is not set: run psql with -v ON_ERROR_STOP=1 to stop at the first error.",
            string.Empty,
            "\\ir schema.sql");

        foreach (var name in dataFileNames)
        {
            driver.Comment("\\ir " + name);
        }
    }

    /// <summary>
    /// Names the table whose parts a file holds, when it holds one table's blocks and that table was split; a
    /// file that packs several tables says nothing (D9).
    /// </summary>
    private static string? IsPartOf(ClosedFile file, IReadOnlyDictionary<TableRef, int> parts) =>
        file.Tables.Count == 1 && parts.TryGetValue(file.Tables[0], out var written) && written > 1
            ? file.Tables[0].ToString()
            : null;

    /// <summary>
    /// Removes exactly the target names this pass had already replaced, so a refused move leaves the folder
    /// holding the earlier store's files wherever this pass did not touch them (FR11).
    /// </summary>
    private static void RemoveCommitted(IReadOnlyList<string> committed)
    {
        foreach (var path in committed)
        {
            DeleteFile(path);
        }
    }

    /// <summary>
    /// Removes every name this pass owns and did not write: the other shape entirely, the parts of a family the
    /// run did not produce, and a driver this pass did not write — never anything else a user may have put in
    /// the script folder (D8, FR11).
    /// </summary>
    /// <remarks>
    /// A name the filesystem refuses to remove — an editor holds the stale file open — is reported as one
    /// warning naming it, because a folder that still carries the earlier store's script beside this one is
    /// exactly the state FR11 forbids, and a store that said nothing would claim otherwise. The store itself
    /// stays successful and the catalog is untouched (FR15).
    /// </remarks>
    private static void Prune(
        string directory,
        IReadOnlyList<ScriptDataFile> written,
        bool family,
        bool driverWritten,
        IList<InterchangeIssue> issues)
    {
        var left = new List<string>();

        void Remove(string path, string relative)
        {
            if (!TryDelete(path))
            {
                left.Add(relative);
            }
        }

        if (family)
        {
            // A single file beside a fresh family would be a second, stale copy with nothing saying so.
            Remove(Path.Combine(directory, DataFileName), DataRelativePath);
        }

        if (!driverWritten)
        {
            // A driver this pass did not write includes parts this pass may have removed: neither a one-file
            // family nor a single data.sql has anything to run with one command (FR10).
            Remove(Path.Combine(directory, LoadFileName), LoadRelativePath);
        }

        var kept = written
            .Select(file => Path.GetFileName(file.RelativePath))
            .ToHashSet(StringComparer.Ordinal);

        var dataDirectory = Path.Combine(directory, DataDirectoryName);
        var readable = !Directory.Exists(dataDirectory);

        try
        {
            if (Directory.Exists(dataDirectory))
            {
                // A part this run did not write is stale: a catalog that now needs three files must not leave
                // the fourth and fifth of an earlier store for the name-order run to execute (D8).
                foreach (var path in Directory.EnumerateFiles(dataDirectory, "*.sql"))
                {
                    var name = Path.GetFileName(path);

                    if (IsNumbered(name) && !kept.Contains(name))
                    {
                        Remove(path, DataFolderRelativePath + "/" + name);
                    }
                }

                readable = true;
            }
        }
        catch (IOException)
        {
            // A folder that will not be read is reported below rather than failing the store (FR15).
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        if (!readable)
        {
            // Nothing is known about the parts of an earlier store the folder would not list.
            left.Add(DataFolderRelativePath);
        }

        if (left.Count > 0)
        {
            issues.Add(new InterchangeIssue(
                PruneWarningCode,
                EIssueSeverity.Warning,
                "The SQL scripts were written, but these files of an earlier store could not be removed: "
                    + $"{string.Join(", ", left)}. A run that loads the folder would also execute them: delete "
                    + "them by hand."));
        }

        RemoveEmpty(dataDirectory);
    }

    /// <summary>Says whether a file name is one of the numbered parts this pass owns.</summary>
    private static bool IsNumbered(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        return name.Length > 0 && name.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// States what <c>schema.sql</c> is and what a reader may rely on, including which file validates the keys
    /// (D2, D4, FR2, FR6).
    /// </summary>
    /// <remarks>
    /// A family never points a reader at a <c>data.sql</c> it does not write, and a catalog with no rows never
    /// points at a data file it does not have. The single file keeps today's exact words (D4).
    /// </remarks>
    private static void WriteSchemaPreamble(
        ScriptFile file,
        ISqlScriptRenderer renderer,
        bool holdsData,
        bool family)
    {
        var validation = family
            ? "the last data file"
            : holdsData ? DataFileName : SchemaFileName;

        file.Comment(
            "-- DbClone interchange catalog: the schema, as the captured engine wrote it.",
            $"-- Dialect: {renderer.Dialect}",
            "--",
            "-- Every statement in this file can be executed again: a second run creates no object,",
            "-- drops nothing that holds data and overwrites nothing.",
            "--",
            "-- A table that already exists is left as it is: no column, key or check is reconciled.",
            "-- A view whose column list changed fails with the server's own message naming it.",
            $"-- Foreign keys are added NOT VALID and validated at the end of {validation}.",
            "-- No role, grant, policy or ownership is written.",
            "-- An extension is installed by its name and the schema it was found in; the version the",
            "-- source carried is not pinned.",
            "-- A sequence keeps the position its own declaration gives it: a catalog records no",
            "-- current value.",
            "-- A sequence an identity column created is not written: the column's declaration",
            "-- creates it, and writing it here as well would leave a second, unused sequence behind.");
        file.Comment(string.Empty);
    }

    /// <summary>States what the data files are and what a reader may rely on (D2, D4, FR6).</summary>
    /// <remarks>
    /// The single file keeps today's exact words; a family speaks per block, because one of its files may hold
    /// nine whole tables or part of one (D4).
    /// </remarks>
    private static void WriteDataPreamble(ScriptFile file, ISqlScriptRenderer renderer, bool family)
    {
        if (!family)
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
            return;
        }

        file.Comment(
            "-- DbClone interchange catalog: the rows, as the captured engine wrote them.",
            $"-- Dialect: {renderer.Dialect}",
            "--",
            "-- Run schema.sql first, then these files in name order — or run load.psql, which does it.",
            "-- Every statement in these files can be executed again: a row that is already on the target",
            "-- is skipped, never overwritten and never duplicated.",
            "-- Each block is its own transaction — a table larger than one file is several — so a failure",
            "-- leaves every earlier block complete.",
            "-- Foreign keys were added NOT VALID and are validated at the end of the last data file.");
        file.Comment(string.Empty);
    }

    /// <summary>
    /// States the cycles the catalog holds, because these files cannot load one and a reader deserves to know
    /// before running them rather than from a key violation (D6).
    /// </summary>
    private static void WriteCycleNote(
        ScriptFile file,
        IReadOnlyList<RelationMap.CyclicGroup> cycles,
        bool family)
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
            family
                ? "-- not committed. The validation at the end of the last data file fails if the rows truly break a key."
                : "-- not committed. The validation at the end of the file fails if the rows truly break a key.");

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
        var extensions = await reader.ReadSectionAsync<ExtensionDefinition>("extensions", ct);
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
            extensions,
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
    /// Removes the scratch files this pass opened — both shapes' — and then the folders when nothing else is
    /// in them: the target names are left exactly as they were found, which is what makes a cancelled or
    /// failed pass cost the scripts of an earlier store nothing (D12, FR11).
    /// </summary>
    private static void DiscardScratch(string directory)
    {
        DeleteFile(Path.Combine(directory, SchemaFileName) + TempSuffix);
        DeleteFile(Path.Combine(directory, DataFileName) + TempSuffix);
        DeleteFile(Path.Combine(directory, LoadFileName) + TempSuffix);

        var dataDirectory = Path.Combine(directory, DataDirectoryName);

        try
        {
            if (Directory.Exists(dataDirectory))
            {
                // A family's scratch names carry the ordinal alone: a digits-and-suffix name is one this pass,
                // or a crashed earlier one, left behind, while any other .tmp is the user's (D8, FR11).
                foreach (var path in Directory.EnumerateFiles(dataDirectory, "*" + TempSuffix))
                {
                    if (IsNumbered(path))
                    {
                        DeleteFile(path);
                    }
                }
            }
        }
        catch (IOException)
        {
            // A folder that will not be read is left as it is.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        RemoveEmpty(dataDirectory);
        RemoveEmpty(directory);
    }

    /// <summary>
    /// Removes a folder when nothing is left in it, treating a filesystem that will not as nothing to report:
    /// an empty folder is inert, because nothing reads one.
    /// </summary>
    private static void RemoveEmpty(string directory)
    {
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
            // A folder that will not go is inert: nothing reads an empty folder.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>
    /// Deletes one file and says whether it is gone, because the pass that prunes has to report a name it could
    /// not remove (FR11). A name that is not there is already gone.
    /// </summary>
    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes one file, treating a filesystem that will not as nothing to report.</summary>
    private static void DeleteFile(string path) => TryDelete(path);

    /// <summary>Spells a stage the way a refusal reads.</summary>
    private static string Screen(SchemaStage stage) => stage.ToString().ToLowerInvariant();

    /// <summary>
    /// One script file, written content-then-move: the text lands in <c>&lt;scratch name&gt;.tmp</c>, that
    /// stream is closed, and only then does the move replace the target inside the same folder (D12).
    /// </summary>
    /// <remarks>
    /// The target is never opened for writing, so no failure — a full disk, a killed process, an exception
    /// between flush and move — can leave a truncated script at the name a user would run. A reader sees
    /// either the previous complete text or the new complete text. A numbered file of a family is written
    /// under its ordinal alone, because the width of its final name is unknown until the count is (D3, D8).
    /// </remarks>
    private sealed class ScriptFile : IAsyncDisposable
    {
        private readonly FileStream _stream;
        private readonly StreamWriter _writer;
        private readonly string _directory;
        private readonly string _scratchName;
        private readonly string _tempPath;
        private bool _closed;

        /// <summary>Initializes a new instance of the <see cref="ScriptFile"/> class.</summary>
        /// <param name="directory">The folder the file belongs to.</param>
        /// <param name="scratchName">
        /// The name the content is written under before the move, which is also the target name when nothing
        /// else is given at publishing time.
        /// </param>
        internal ScriptFile(string directory, string scratchName)
        {
            _directory = directory;
            _scratchName = scratchName;
            _tempPath = Path.Combine(directory, scratchName) + TempSuffix;
            _stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new StreamWriter(_stream, Utf8NoBom) { NewLine = LineBreak };
        }

        /// <summary>Gets how many executable statements have been written.</summary>
        internal int Statements { get; private set; }

        /// <summary>Gets how many bytes the text takes, which is what the budget is measured against (D6).</summary>
        internal long Bytes { get; private set; }

        /// <summary>Gets how many rows the statements insert, summed from the batches the renderer reported (D9).</summary>
        internal long Rows { get; private set; }

        /// <summary>Writes lines that are not statements, such as comments or the driver's commands.</summary>
        internal void Comment(params string[] lines)
        {
            foreach (var line in lines)
            {
                _writer.WriteLine(line);
                Bytes += Utf8NoBom.GetByteCount(line) + LineBreak.Length;
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

            Bytes += Utf8NoBom.GetByteCount(statement) + (2 * LineBreak.Length);

            if (!statement.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                Statements++;
            }
        }

        /// <summary>Adds the rows one batch carried, so the file states what it holds rather than what was planned (D9).</summary>
        /// <param name="rows">The rows the batch inserted.</param>
        internal void AddRows(int rows) => Rows += rows;

        /// <summary>Closes the content, leaving it at its scratch name.</summary>
        internal async Task CloseAsync()
        {
            if (_closed)
            {
                return;
            }

            try
            {
                await _writer.FlushAsync();
            }
            finally
            {
                // The flag is set only once the stream is released: a flush that throws must not leave a
                // handle open that the scratch cleanup then cannot delete (D12).
                await _writer.DisposeAsync();
                _closed = true;
            }
        }

        /// <summary>Moves the closed content onto its target name, replacing what is there (D12).</summary>
        /// <param name="targetName">
        /// The name to move onto, relative to the file's folder; the scratch name when not given.
        /// </param>
        /// <returns>The target path, which is what the pass records as committed (D8, FR11).</returns>
        internal string MoveToTarget(string? targetName = null)
        {
            var target = Path.Combine(_directory, targetName ?? _scratchName);

            File.Move(_tempPath, target, overwrite: true);

            return target;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_closed)
            {
                return;
            }

            await _writer.DisposeAsync();

            _closed = true;
        }
    }

    /// <summary>One data file the pass closed: its ordinal, the file and the tables whose blocks it holds (D9).</summary>
    private sealed record ClosedFile(int Ordinal, ScriptFile File, IReadOnlyList<TableRef> Tables);

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
        IReadOnlyList<ExtensionDefinition> Extensions,
        IReadOnlyList<SequenceDefinition> Sequences,
        IReadOnlyList<TableDefinition> Tables,
        IReadOnlyList<RoutineDefinition> Routines,
        IReadOnlyList<ViewDefinition> Views,
        IReadOnlyList<NativeObjectDefinition> NativeObjects,
        bool HoldsData);
}
