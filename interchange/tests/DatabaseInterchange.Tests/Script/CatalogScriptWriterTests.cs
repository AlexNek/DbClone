using System.Globalization;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;
using DatabaseInterchange.Script;
using DatabaseInterchange.Tests.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Script;

/// <summary>
/// The product's half of the pass: the folder, the two file names, the order the statements run in, where
/// the tail lands, and what a refusal or a refused move costs the catalog (D8, D11, D12).
/// </summary>
/// <remarks>
/// Every fixture is a real catalog on disk, written by the real writer and read back by the real reader, and
/// the SQL comes from a recording double. What is asserted here is the product's decisions only; whether the
/// text is PostgreSQL is the adapter suite's question.
/// </remarks>
public sealed class CatalogScriptWriterTests : IDisposable
{
    private const string Dialect = "postgresql";

    /// <summary>The budget that asks for today's single <c>data.sql</c>, whatever the size (FR13).</summary>
    private const long NoBudget = 0;

    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeScriptWriter_" + Guid.NewGuid().ToString("N"));

    private readonly CatalogScriptWriter _sut = new();

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_LandsBothFilesInTheScriptFolder_AndStatesTheirMeasures()
    {
        // Arrange — two tables with two rows each, in an interchange catalog of the engine's own dialect.
        await CreateCatalogAsync();
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], issues, NoBudget, CancellationToken.None);

        // Assert (FR1, FR9) — both files exist at the names the result states, the sizes are the files'
        // own lengths, and nothing was reported: the pass wrote what it was asked for.
        result.Succeeded.Should().BeTrue();
        result.SchemaPath.Should().Be(CatalogScriptWriter.SchemaRelativePath);
        result.DataFiles.Should().ContainSingle().Which.RelativePath.Should().Be(CatalogScriptWriter.DataRelativePath);
        File.Exists(SchemaFile()).Should().BeTrue();
        File.Exists(DataFile()).Should().BeTrue();
        result.SchemaBytes.Should().Be(new FileInfo(SchemaFile()).Length);
        result.DataFiles[0].Bytes.Should().Be(new FileInfo(DataFile()).Length);
        result.SchemaBytes.Should().BeGreaterThan(0);
        result.SchemaStatements.Should().BeGreaterThan(0);
        result.DataFiles[0].Statements.Should().BeGreaterThan(0);
        result.Rows.Should().Be(4, "the fixture holds two rows in each of two tables");
        issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_RunsEverySchemaStageBeforeTheFirstTable()
    {
        // Arrange
        await CreateCatalogAsync();
        var renderer = new RecordingScriptRenderer();

        // Act
        await _sut.WriteAsync(_catalogPath, renderer, [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);

        // Assert (D8) — the order a restore applies, and no row statement inside it: a script may never run
        // in an order a restore does not.
        var calls = renderer.Calls;
        var firstRow = calls.ToList().FindIndex(call => call.StartsWith("data ", StringComparison.Ordinal));
        var lastSchema = calls.ToList().FindLastIndex(call => call.StartsWith("schema ", StringComparison.Ordinal));

        firstRow.Should().BeGreaterThan(0);
        lastSchema.Should().BeLessThan(firstRow, "every stage of the schema is written before the first table's rows");
        calls.ToList().IndexOf("tail").Should().BeGreaterThan(
            lastSchema, "the tail is rendered once the schema it finishes is known, and written last");
        calls.Should().Contain("schema Tables:customers");
        calls.Should().Contain("schema Tables:orders");
    }

    [Fact]
    public async Task WriteAsync_PutsTheTailInTheDataFile_WhenTheCatalogHoldsRows()
    {
        // Arrange — the tail is the only text this double answers with that is easy to spot.
        await CreateCatalogAsync();

        // Act
        await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);

        // Assert (D2, FR6) — the file written last carries the statements that make the keys true, so a
        // reader who runs schema.sql alone is never told a foreign key is enforced when it is not.
        (await File.ReadAllTextAsync(DataFile())).Should().Contain("VALIDATE ALL KEYS;");
        (await File.ReadAllTextAsync(SchemaFile())).Should().NotContain("VALIDATE ALL KEYS;");
    }

    [Fact]
    public async Task WriteAsync_WritesOnlyTheSchemaFile_WhenTheCatalogHoldsNoRows()
    {
        // Arrange — a schema-only capture: the manifest records zero rows, so there is no data file at all.
        await CreateCatalogAsync(withRows: false);
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], issues, NoBudget, CancellationToken.None);

        // Assert (D2) — with no second file, the schema file is the one written last and carries the tail.
        result.Succeeded.Should().BeTrue();
        result.DataFiles.Should().BeEmpty();
        result.Rows.Should().Be(0);
        File.Exists(DataFile()).Should().BeFalse("an empty BEGIN/COMMIT would only pad the folder");
        (await File.ReadAllTextAsync(SchemaFile())).Should().Contain("VALIDATE ALL KEYS;");
        issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_WritesAParentBeforeAChildWhoseNameSortsFirst()
    {
        // Arrange — a child whose name sorts before the table its key points at, which is the order the
        // catalog's sections are written in and one a load cannot use (D6).
        await CreateCatalogAsync(
            snapshot: ReportModel.Snapshot(
                ReportModel.KeyedTable("public", "a_orders") with
                {
                    ForeignKeys =
                    [
                        ReportModel.Reference(
                            "public", "a_orders", "fk_a_orders_customer", "public", "z_customers",
                            ["id"], ["id"]),
                    ],
                },
                ReportModel.KeyedTable("public", "z_customers")));
        var renderer = new RecordingScriptRenderer();

        // Act
        await _sut.WriteAsync(_catalogPath, renderer, [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);

        // Assert — the block whose rows a key already checks comes first, whatever the names sort like.
        renderer.Calls
            .Where(call => call.StartsWith("data ", StringComparison.Ordinal))
            .Should().Equal("data public.z_customers:2", "data public.a_orders:2");
    }

    [Fact]
    public async Task WriteAsync_StatesTheCountTheCatalogRecordedForEveryTableItWrites()
    {
        // Arrange — the count a block's banner needs comes from the catalog, not from a list the writer
        // holds, which is what lets the rows stream to the renderer as they are read (FR9).
        await CreateCatalogAsync();
        var renderer = new RecordingScriptRenderer();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, renderer, [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);

        // Assert
        renderer.DeclaredRowCounts.Should().Contain("public.customers", 2L).And.Contain("public.orders", 2L);
        result.Rows.Should().Be(4, "every row that passed the tally is counted once");
    }

    [Fact]
    public async Task WriteAsync_LeavesThePreviousScriptByteForByte_WhenTheMoveIsRefused()
    {
        // Arrange — a complete pair from an earlier store, and the schema file held open so the move is
        // refused by the filesystem itself rather than by a substitute.
        await CreateCatalogAsync();
        await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);
        var previousSchema = await File.ReadAllBytesAsync(SchemaFile());
        var previousData = await File.ReadAllBytesAsync(DataFile());
        var issues = new List<InterchangeIssue>();

        ScriptResult result;
        using (var held = new FileStream(SchemaFile(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Act
            result = await _sut.WriteAsync(
                _catalogPath, new RecordingScriptRenderer(), [], issues, NoBudget, CancellationToken.None);
        }

        // Assert (D12) — the target is never opened for writing, so a refused move costs the text that was
        // there nothing: both files are byte for byte what the earlier store wrote.
        result.Succeeded.Should().BeFalse();
        result.Refusal!.Code.Should().Be(CatalogScriptWriter.WriteFailureCode);
        issues.Should().ContainSingle().Which.Code.Should().Be(CatalogScriptWriter.WriteFailureCode);
        (await File.ReadAllBytesAsync(SchemaFile())).Should().Equal(previousSchema);
        (await File.ReadAllBytesAsync(DataFile())).Should().Equal(previousData);
        Directory.GetFiles(Path.GetDirectoryName(SchemaFile())!, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_WhenCancelled_LeavesTheEarlierStoresScriptsInPlace()
    {
        // Arrange — a complete pair from an earlier store, and the pass being cancelled (FR12, D12).
        await CreateCatalogAsync();
        await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);
        var previousSchema = await File.ReadAllBytesAsync(SchemaFile());
        var previousData = await File.ReadAllBytesAsync(DataFile());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], issues, NoBudget, cts.Token);

        // Assert — a cancel costs the pass only what the pass opened: the target names are still the earlier
        // store's, byte for byte, and no scratch file is left beside them.
        result.Cancelled.Should().BeTrue();
        issues.Should().BeEmpty("nothing malfunctioned, so nothing is reported as one");
        (await File.ReadAllBytesAsync(SchemaFile())).Should().Equal(previousSchema);
        (await File.ReadAllBytesAsync(DataFile())).Should().Equal(previousData);
        Directory.GetFiles(ScriptFolder(), "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_WhenTheRowsAreRefused_LeavesTheEarlierStoresScriptsInPlace()
    {
        // Arrange — a complete pair from an earlier store, and a renderer that refuses one table's rows,
        // which is a refusal only reached once both scratch files are open (D12).
        await CreateCatalogAsync();
        await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);
        var previousSchema = await File.ReadAllBytesAsync(SchemaFile());
        var previousData = await File.ReadAllBytesAsync(DataFile());
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath,
            new RecordingScriptRenderer { UnscriptableRows = "orders" },
            [],
            issues,
            NoBudget,
            CancellationToken.None);

        // Assert — the scratch pair dies with the refusal, and neither target name was touched.
        result.Refusal!.Reason.Should().Contain("orders");
        issues.Should().ContainSingle().Which.Code.Should().Be(CatalogScriptWriter.RefusalCode);
        (await File.ReadAllBytesAsync(SchemaFile())).Should().Equal(previousSchema);
        (await File.ReadAllBytesAsync(DataFile())).Should().Equal(previousData);
        Directory.GetFiles(ScriptFolder(), "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_WhenCancelled_WritesNoFileAndReportsNothing()
    {
        // Arrange — the catalog on disk is finished; only the pass is being cancelled (FR12).
        await CreateCatalogAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], issues, NoBudget, cts.Token);

        // Assert — a cancel is a stop, not a failure: no file, no issue, and no half-written folder.
        result.Cancelled.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        issues.Should().BeEmpty("nothing malfunctioned, so nothing is reported as one");
        Directory.Exists(ScriptFolder()).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_RefusesACatalogCapturedFromAnotherDialect()
    {
        // Arrange — a catalog the one installed adapter cannot speak for (D11).
        await CreateCatalogAsync(dialect: "mysql");
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], issues, NoBudget, CancellationToken.None);

        // Assert — one warning naming both dialects, no folder, and no exception: a script is the source
        // engine's own SQL and is never translated.
        result.SchemaPath.Should().BeNull();
        result.Refusal!.Code.Should().Be(CatalogScriptWriter.RefusalCode);
        result.Refusal.Reason.Should().Contain("mysql").And.Contain(Dialect);
        issues.Should().ContainSingle().Which.Code.Should().Be(CatalogScriptWriter.RefusalCode);
        issues[0].Severity.Should().Be(EIssueSeverity.Warning);
        Directory.Exists(ScriptFolder()).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_RefusesADirectFormatCatalog()
    {
        // Arrange — a same-type transfer that records the source model instead of the interchange sections,
        // so it carries none of the captured text a script is written from.
        await CreateCatalogAsync(directFormat: true);
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], issues, NoBudget, CancellationToken.None);

        // Assert
        result.Refusal!.Reason.Should().Contain("interchange sections");
        issues.Should().ContainSingle().Which.Code.Should().Be(CatalogScriptWriter.RefusalCode);
        Directory.Exists(ScriptFolder()).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_RefusesWhenAnObjectCannotBeScripted_AndNamesIt()
    {
        // Arrange — one table the renderer has no text for, which is how a foreign table or an exclusion
        // constraint surfaces: the restore refuses it too, so a file that skipped it would lie (D11).
        await CreateCatalogAsync();
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer { Unscriptable = "orders" }, [], issues, NoBudget, CancellationToken.None);

        // Assert — the refusal names the object rather than the stage it happened to be in, and nothing at
        // all was written.
        result.Refusal!.Reason.Should().Contain("orders");
        issues.Should().ContainSingle().Which.Code.Should().Be(CatalogScriptWriter.RefusalCode);
        Directory.Exists(ScriptFolder()).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_WritesTheSameBytesTwice_ForTheSameCatalog()
    {
        // Arrange — FR11's determinism, measured on the bytes rather than on the model.
        await CreateCatalogAsync();

        // Act
        await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);
        var firstSchema = await File.ReadAllBytesAsync(SchemaFile());
        var firstData = await File.ReadAllBytesAsync(DataFile());
        await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);

        // Assert
        (await File.ReadAllBytesAsync(SchemaFile())).Should().Equal(firstSchema);
        (await File.ReadAllBytesAsync(DataFile())).Should().Equal(firstData);
    }

    [Fact]
    public async Task WriteAsync_WritesUtf8WithoutBom_AndOnlyLf()
    {
        // Arrange
        await CreateCatalogAsync();

        // Act
        await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);

        // Assert (FR11) — a file a user commits or diffs must not carry a byte nobody asked for.
        foreach (var path in new[] { SchemaFile(), DataFile() })
        {
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[..3].Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "a BOM is not SQL");
            bytes.Should().NotContain((byte)'\r', "one line break, whatever the platform");
            bytes[^1].Should().Be((byte)'\n');
        }
    }

    [Fact]
    public async Task WriteAsync_WithABudget_LandsTheFamilyInTheDataFolder()
    {
        // Arrange — fifty rows of two hundred characters in each of two tables, against a budget far below
        // what they weigh, so the planner has to cut them into parts (D2, D3).
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(_catalogPath, Padded(), [], issues, FamilyBudget, CancellationToken.None);

        // Assert (FR1, FR5, D7) — the family is numbered files in data/, the single file of the other shape is
        // gone, and every row the catalog holds is still inserted once.
        result.Succeeded.Should().BeTrue();
        result.Profile.Should().Be(EScriptProfile.Giants);
        result.BudgetBytes.Should().Be(FamilyBudget);
        result.DataFiles.Count.Should().BeGreaterThan(1);
        result.DataFiles.Should().OnlyContain(
            file => file.RelativePath.StartsWith(CatalogScriptWriter.DataFolderRelativePath + "/", StringComparison.Ordinal));
        result.DataFiles.Select(file => Path.GetFileName(file.RelativePath))
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
        result.DataFiles.Select(file => file.RelativePath).Should().OnlyHaveUniqueItems();
        result.Rows.Should().Be(100);
        result.LoadPath.Should().Be(CatalogScriptWriter.LoadRelativePath);
        File.Exists(DataFile()).Should().BeFalse("the family replaces data.sql");
        Directory.Exists(DataDirectory()).Should().BeTrue();
        issues.Should().BeEmpty();

        // Assert (D2, D4) — the preamble and the header belong to the first file; a continuation starts with
        // the block it continues rather than repeating what the reader already read.
        var first = await File.ReadAllTextAsync(Path.Combine(_catalogPath, result.DataFiles[0].RelativePath));
        var second = await File.ReadAllTextAsync(Path.Combine(_catalogPath, result.DataFiles[1].RelativePath));

        first.Should().Contain("SET TIME ZONE 'UTC';").And.Contain("Each block is its own transaction");
        second.Should().NotContain("SET TIME ZONE 'UTC';").And.NotContain("Each block is its own transaction");
    }

    [Fact]
    public async Task WriteAsync_WithABudget_KeepsEveryFileWithinIt()
    {
        // Arrange
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);

        // Act
        var result = await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);

        // Assert (FR5, D6) — the budget bounds the real bytes, not the estimate: no file wrote a statement it
        // could not hold, and the size the result states is the file's own length.
        foreach (var file in result.DataFiles)
        {
            new FileInfo(Path.Combine(_catalogPath, file.RelativePath)).Length.Should().Be(file.Bytes);
            file.Bytes.Should().BeLessThanOrEqualTo(
                FamilyBudget, $"{file.RelativePath} holds only statements the budget allowed");
        }
    }

    [Fact]
    public async Task WriteAsync_WhenOneStatementExceedsTheBudget_KeepsItAloneInItsFile()
    {
        // Arrange — every insert is larger than a whole file, which no cut can make smaller (FR5, FR6).
        await CreateCatalogAsync(rows: 5, rowTextSize: 200);

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, Padded(statementPadding: 5000), [], new List<InterchangeIssue>(), 1024, CancellationToken.None);

        // Assert (D6) — a statement is never split, so a file that cannot hold two holds at most one; the
        // first file carries the preamble, and it ends there when not even one statement fits beside it.
        result.DataFiles.Count.Should().BeGreaterThan(1);
        result.DataFiles.Should().OnlyContain(
            file => Inserts(Path.Combine(_catalogPath, file.RelativePath)) <= 1);
        result.DataFiles.Skip(1).Should().OnlyContain(
            file => Inserts(Path.Combine(_catalogPath, file.RelativePath)) == 1);
        result.DataFiles.Sum(file => Inserts(Path.Combine(_catalogPath, file.RelativePath)))
            .Should().Be(10, "five rows in each of two tables, each a statement of its own");
    }

    [Fact]
    public async Task WriteAsync_WhenTheNextBlocksFirstStatementWouldNotFit_EndsTheFileBeforeThatBlock()
    {
        // Arrange — every table is estimated well inside the budget, so the plan packs three of them into one
        // file; the padding then makes the real statements larger than the estimate, which is the case D6
        // expects the writer to absorb by spilling. The file that ends mid-block has room for a block but not
        // for the next one, and that is where a block opened too early overruns the budget (FR5, FR6).
        await CreateCatalogAsync(snapshot: FourTables(), rows: 3, rowTextSize: 100);
        const long budget = 2000;

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, Padded(statementPadding: 1100), [], new List<InterchangeIssue>(), budget, CancellationToken.None);

        // Assert (D6) — a file passes the budget only for one statement it cannot split, and no file holds an
        // opener it could not fill: the cut is made before the block, never after the insert that overran it.
        result.DataFiles.Count.Should().BeGreaterThan(2);

        foreach (var file in result.DataFiles)
        {
            var path = Path.Combine(_catalogPath, file.RelativePath);

            Inserts(path).Should().BeGreaterThan(
                0, $"{file.RelativePath} must not end with a block that inserts nothing");

            if (file.Bytes > budget)
            {
                Inserts(path).Should().Be(
                    1, $"{file.RelativePath} may pass the budget only for one statement");
            }
        }
    }

    [Fact]
    public async Task WriteAsync_KeepsTheTailInsideTheBudgetOfTheLastFile()
    {
        // Arrange — a tail larger than what a file's envelope leaves over: a last file filled to the brim
        // would carry it past the budget unless the tail's bytes were reserved while the file was written.
        await CreateCatalogAsync(snapshot: FourTables(), rows: 3, rowTextSize: 100);
        const long budget = 2000;
        var renderer = new RecordingScriptRenderer { BatchRows = 1, StatementPadding = 500, TailPadding = 1100 };

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, renderer, [], new List<InterchangeIssue>(), budget, CancellationToken.None);

        // Assert (FR5, FR9, D6) — the budget bounds every file including the one that carries the preamble and
        // the one that carries the validation, every row is inserted once, and the tail is in the last file.
        result.DataFiles.Count.Should().BeGreaterThan(1);

        foreach (var file in result.DataFiles)
        {
            file.Bytes.Should().BeLessThanOrEqualTo(
                budget, $"{file.RelativePath} holds only what the budget left room for");
        }

        Inserts(Path.Combine(_catalogPath, result.DataFiles[0].RelativePath)).Should().Be(
            0, "the first file's preamble is counted before its first block is opened");
        result.DataFiles.Sum(file => Inserts(Path.Combine(_catalogPath, file.RelativePath))).Should().Be(12);

        (await File.ReadAllTextAsync(Path.Combine(_catalogPath, result.DataFiles[^1].RelativePath)))
            .Should().Contain("VALIDATE ALL KEYS;");

        foreach (var earlier in result.DataFiles.Take(result.DataFiles.Count - 1))
        {
            (await File.ReadAllTextAsync(Path.Combine(_catalogPath, earlier.RelativePath)))
                .Should().NotContain("VALIDATE ALL KEYS;");
        }
    }

    [Fact]
    public async Task WriteAsync_WhenAFamilyLandsInOneFile_RemovesTheDriverOfAnEarlierOne()
    {
        // Arrange — a family of several files and its driver, then a store whose plan is still a Giants plan
        // (the estimate passes the budget) but whose actual bytes fit one file. The earlier driver includes a
        // part this pass removes, and nothing this pass writes replaces it (FR10, FR11).
        var oneTable = ReportModel.Snapshot(ReportModel.KeyedTable("public", "alpha"));
        await CreateCatalogAsync(snapshot: oneTable, rows: 50, rowTextSize: 200);
        var family = await _sut.WriteAsync(
            _catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);

        family.LoadPath.Should().Be(CatalogScriptWriter.LoadRelativePath);

        // Act
        var single = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), 5000, CancellationToken.None);

        // Assert — one numbered file and no driver: a driver that includes a part which is gone is worse than
        // none, and no stale part of the earlier family is left for a name-order run to execute.
        single.Profile.Should().Be(EScriptProfile.Giants);
        single.DataFiles.Should().ContainSingle();
        single.LoadPath.Should().BeNull();
        File.Exists(LoadFile()).Should().BeFalse("a one-file family needs no driver");
        Directory.GetFiles(DataDirectory(), "*.sql").Select(Path.GetFileName)
            .Should().BeEquivalentTo(single.DataFiles.Select(file => Path.GetFileName(file.RelativePath)));
    }

    [Fact]
    public async Task WriteAsync_WhenAStaleFileWillNotGo_NamesIt_AndStillSucceeds()
    {
        // Arrange — a single-file store, then the family's store while the stale data.sql is held open, which
        // is how the filesystem refuses the prune (FR11, FR15).
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);
        await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);
        var issues = new List<InterchangeIssue>();

        ScriptResult result;
        using (var stream = new FileStream(DataFile(), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Act
            result = await _sut.WriteAsync(
                _catalogPath, Padded(), [], issues, FamilyBudget, CancellationToken.None);
        }

        // Assert — the scripts are this pass's and the store succeeded, and the one name that would not go is
        // named rather than left as a silent second copy of the rows.
        result.Succeeded.Should().BeTrue();
        result.DataFiles.Count.Should().BeGreaterThan(1);
        File.Exists(DataFile()).Should().BeTrue("an open file is one the prune could not remove");
        issues.Should().ContainSingle().Which.Code.Should().Be(CatalogScriptWriter.PruneWarningCode);
        issues[0].Message.Should().Contain(CatalogScriptWriter.DataRelativePath);
    }

    [Fact]
    public async Task WriteAsync_WhenCancelled_LeavesTheUsersTempFilesAlone()
    {
        // Arrange — a file of the user's in data/ beside a scratch name a crashed earlier run could have left.
        await CreateCatalogAsync();
        Directory.CreateDirectory(DataDirectory());
        var mine = Path.Combine(DataDirectory(), "notes.tmp");
        var crashed = Path.Combine(DataDirectory(), "0007.tmp");
        await File.WriteAllTextAsync(mine, "my own file");
        await File.WriteAllTextAsync(crashed, "a scratch name this pass owns");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), NoBudget, cancelled.Token);

        // Assert (FR11, D8) — a cancel before the first byte costs the folder nothing of the user's, and still
        // sweeps what a crashed earlier pass left under a name this pass could have written.
        result.Cancelled.Should().BeTrue();
        File.Exists(mine).Should().BeTrue("the pass owns data/<digits>.tmp, never every .tmp");
        File.Exists(crashed).Should().BeFalse("a digits-and-suffix name is the pass's own scratch");
        Directory.Exists(DataDirectory()).Should().BeTrue("the folder still holds the user's file");
    }

    [Fact]
    public async Task WriteAsync_PutsTheTailInTheLastFileOfTheFamily()
    {
        // Arrange
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);

        // Act
        var result = await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);

        // Assert (FR9, D2) — the tail belongs to the file written last, and to that file only.
        var last = result.DataFiles[^1].RelativePath;

        (await File.ReadAllTextAsync(Path.Combine(_catalogPath, last))).Should().Contain("VALIDATE ALL KEYS;");

        foreach (var earlier in result.DataFiles.Take(result.DataFiles.Count - 1))
        {
            (await File.ReadAllTextAsync(Path.Combine(_catalogPath, earlier.RelativePath)))
                .Should().NotContain("VALIDATE ALL KEYS;");
        }
    }

    [Fact]
    public async Task WriteAsync_WritesTheDriverFromTheFilesItWrote()
    {
        // Arrange
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);

        // Act
        var result = await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);

        // Assert (FR10, D7) — the driver includes the schema first and then each file in run order, and it
        // lists exactly the files that exist rather than the ones the plan expected.
        var includes = (await File.ReadAllLinesAsync(LoadFile()))
            .Where(line => line.StartsWith("\\ir ", StringComparison.Ordinal))
            .ToList();

        includes[0].Should().Be("\\ir schema.sql");
        includes.Skip(1).Should().Equal(
            result.DataFiles.Select(file => "\\ir " + CatalogScriptWriter.DataDirectoryName + "/" + Path.GetFileName(file.RelativePath)));
        includes.Should().HaveCount(result.DataFiles.Count + 1);
    }

    [Fact]
    public async Task WriteAsync_SwitchingShape_PrunesTheOtherOne_AndTouchesNothingElse()
    {
        // Arrange — an earlier single-file store, and two files of the user's own in the folder (FR11, D8).
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);
        await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);
        var notes = Path.Combine(ScriptFolder(), "notes.txt");
        var keep = Path.Combine(DataDirectory(), "keep.me");

        await File.WriteAllTextAsync(notes, "my own note");
        Directory.CreateDirectory(DataDirectory());
        await File.WriteAllTextAsync(keep, "my own file");

        // Act — the family replaces the single file.
        await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);

        // Assert — the other shape is gone, and nothing this pass does not own was touched.
        File.Exists(DataFile()).Should().BeFalse("a stale data.sql beside a family is a second, older copy");
        File.Exists(LoadFile()).Should().BeTrue();
        File.Exists(notes).Should().BeTrue();
        File.Exists(keep).Should().BeTrue();

        // Act — the single file replaces the family again.
        var again = await _sut.WriteAsync(
            _catalogPath, Padded(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);

        // Assert (D8) — the family and its driver are removed, down to the empty folder, and only the
        // user's files are left behind.
        again.DataFiles.Should().ContainSingle();
        File.Exists(LoadFile()).Should().BeFalse();
        Directory.GetFiles(DataDirectory(), "*.sql").Should().BeEmpty("every part of the family belonged to this pass");
        File.Exists(keep).Should().BeTrue();
        File.Exists(notes).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_WithFewerParts_LeavesNoStalePart()
    {
        // Arrange — a small budget first, so the family of the second store must be shorter (D8, FR11).
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);
        var many = await _sut.WriteAsync(
            _catalogPath, Padded(), [], new List<InterchangeIssue>(), 2048, CancellationToken.None);

        // Act
        var few = await _sut.WriteAsync(
            _catalogPath, Padded(), [], new List<InterchangeIssue>(), 8192, CancellationToken.None);

        // Assert — a name-order run must never execute a part an earlier store wrote and this one did not.
        few.DataFiles.Count.Should().BeGreaterThan(1);
        few.DataFiles.Count.Should().BeLessThan(many.DataFiles.Count);
        Directory.GetFiles(DataDirectory(), "*.sql").Select(Path.GetFileName)
            .Should().BeEquivalentTo(few.DataFiles.Select(file => Path.GetFileName(file.RelativePath)));
    }

    [Fact]
    public async Task WriteAsync_TheFamilySchemaFileDiffersFromTheSingleOneInOneClauseOnly()
    {
        // Arrange — the same catalog stored twice, once whole and once as a family (D4).
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);
        await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), NoBudget, CancellationToken.None);
        var single = await File.ReadAllLinesAsync(SchemaFile());

        // Act
        await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);

        // Assert (D4) — only the sentence naming what validates the keys changes; nothing else in the file does.
        var family = await File.ReadAllLinesAsync(SchemaFile());
        var differing = single.Zip(family).Where(pair => pair.First != pair.Second).ToList();

        differing.Should().ContainSingle();
        differing[0].First.Should().Contain("validated at the end of data.sql.");
        differing[0].Second.Should().Contain("validated at the end of the last data file.");
        family.Should().HaveCount(single.Length);
    }

    [Fact]
    public async Task WriteAsync_WhenOneFileWasPlannedAndTheBytesPassTheBudget_StillWritesThatOneFile()
    {
        // Arrange — a plan of one file (the catalog fits the budget) whose padded statements do not fit it:
        // an S1 plan never spills, because the shape was decided before the first byte (D6, FR6).
        await CreateCatalogAsync();
        var result = await _sut.WriteAsync(
            _catalogPath, Padded(statementPadding: 1000), [], new List<InterchangeIssue>(), 200, CancellationToken.None);

        // Assert — one data.sql, no family, no driver, and the result says one file.
        result.Profile.Should().Be(EScriptProfile.Fits);
        result.DataFiles.Should().ContainSingle().Which.RelativePath.Should().Be(CatalogScriptWriter.DataRelativePath);
        result.DataFiles[0].Bytes.Should().BeGreaterThan(200, "the estimate is not a cap");
        result.LoadPath.Should().BeNull();
        Directory.Exists(DataDirectory()).Should().BeFalse();
        File.Exists(LoadFile()).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_WritesTheSameFamilyTwice_ForTheSameCatalog()
    {
        // Arrange — FR11's determinism, measured on every file of a family rather than on the model.
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);

        // Act
        var first = await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);
        var names = first.DataFiles.Select(file => file.RelativePath).ToList();
        var bytes = new List<byte[]>();

        foreach (var name in names.Append(first.SchemaPath!).Append(first.LoadPath!))
        {
            bytes.Add(await File.ReadAllBytesAsync(Path.Combine(_catalogPath, name)));
        }

        var second = await _sut.WriteAsync(_catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);

        // Assert (D12) — the same names, in the same order, carrying the same bytes.
        second.DataFiles.Select(file => file.RelativePath).Should().Equal(names);
        second.SchemaPath.Should().Be(first.SchemaPath);
        second.LoadPath.Should().Be(first.LoadPath);

        var index = 0;
        foreach (var name in names.Append(second.SchemaPath!).Append(second.LoadPath!))
        {
            (await File.ReadAllBytesAsync(Path.Combine(_catalogPath, name))).Should().Equal(bytes[index++]);
        }
    }

    [Fact]
    public async Task WriteAsync_WhenADataFileMoveIsRefused_RemovesExactlyTheNamesItCommitted()
    {
        // Arrange — a complete family from an earlier store, and its first part held open so the second
        // store's move onto that name is refused by the filesystem itself (D8, FR11).
        await CreateCatalogAsync(rows: 50, rowTextSize: 200);
        var previous = await _sut.WriteAsync(
            _catalogPath, Padded(), [], new List<InterchangeIssue>(), FamilyBudget, CancellationToken.None);
        var previousData = new List<byte[]>();

        foreach (var file in previous.DataFiles)
        {
            previousData.Add(await File.ReadAllBytesAsync(Path.Combine(_catalogPath, file.RelativePath)));
        }

        var held = Path.Combine(_catalogPath, previous.DataFiles[0].RelativePath);
        var issues = new List<InterchangeIssue>();

        ScriptResult result;
        using (var stream = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Act
            result = await _sut.WriteAsync(
                _catalogPath, Padded(), [], issues, FamilyBudget, CancellationToken.None);
        }

        // Assert — the schema had already been replaced, so the refusal removes that one name and leaves the
        // earlier family's parts byte for byte; no scratch file survives at either shape.
        result.Succeeded.Should().BeFalse();
        result.Refusal!.Code.Should().Be(CatalogScriptWriter.WriteFailureCode);
        File.Exists(SchemaFile()).Should().BeFalse("the name was committed and then removed, never left partial");

        foreach (var pair in previous.DataFiles.Zip(previousData))
        {
            (await File.ReadAllBytesAsync(Path.Combine(_catalogPath, pair.First.RelativePath))).Should().Equal(pair.Second);
        }

        Directory.GetFiles(ScriptFolder(), "*.tmp").Should().BeEmpty();
        Directory.GetFiles(DataDirectory(), "*.tmp").Should().BeEmpty();
    }

    private const long FamilyBudget = 4096;

    private static RecordingScriptRenderer Padded(int statementPadding = 400) =>
        new() { BatchRows = 1, StatementPadding = statementPadding };

    private static int Inserts(string path) =>
        File.ReadAllLines(path).Count(line => line.StartsWith("INSERT INTO ", StringComparison.Ordinal));

    private string DataDirectory() => Path.Combine(ScriptFolder(), CatalogScriptWriter.DataDirectoryName);

    private string LoadFile() => Path.Combine(ScriptFolder(), CatalogScriptWriter.LoadFileName);

    private string ScriptFolder() => Path.Combine(_catalogPath, CatalogScriptWriter.DirectoryName);

    private string SchemaFile() => Path.Combine(_catalogPath, CatalogScriptWriter.DirectoryName, CatalogScriptWriter.SchemaFileName);

    private string DataFile() => Path.Combine(_catalogPath, CatalogScriptWriter.DirectoryName, CatalogScriptWriter.DataFileName);

    /// <summary>Two related tables, so an ordering test has more than one object per stage.</summary>
    private static InterchangeSnapshot Fixture() =>
        ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            ReportModel.KeyedTable("public", "orders") with
            {
                ForeignKeys =
                [
                    ReportModel.Reference(
                        "public", "orders", "fk_orders_customer", "public", "customers",
                        ["customer_id"], ["id"]),
                ],
            });

    /// <summary>Four unrelated tables of the same shape, so the plan packs more than one into a file (D3).</summary>
    private static InterchangeSnapshot FourTables() =>
        ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "alpha"),
            ReportModel.KeyedTable("public", "bravo"),
            ReportModel.KeyedTable("public", "charlie"),
            ReportModel.KeyedTable("public", "delta"));

    /// <summary>
    /// A real catalog. Interchange format by default, because that is what the exporter writes and the only
    /// shape carrying the captured text a script is made of; <paramref name="directFormat"/> builds the other.
    /// </summary>
    private async Task CreateCatalogAsync(
        bool withRows = true,
        string dialect = Dialect,
        bool directFormat = false,
        InterchangeSnapshot? snapshot = null,
        int rows = 2,
        int rowTextSize = 0)
    {
        snapshot ??= Fixture();

        await using var writer = directFormat
            ? await CatalogWriter.CreateAsync(
                _catalogPath, ReportModel.Producer, overwrite: true, sourceDialect: dialect)
            : await CatalogWriter.CreateInterchangeAsync(
                _catalogPath, ReportModel.Producer, dialect, overwrite: true);

        await writer.WriteSectionAsync("schemas", [ReportModel.Schema("public")]);
        await writer.WriteSectionAsync("tables", snapshot.Tables);

        foreach (var table in snapshot.Tables)
        {
            await writer.WriteTableAsync(
                table, "public", Rows(withRows ? rows : 0, rowTextSize), new ExportOptions { MaxRowsPerChunk = 2 });
        }

        await writer.CompleteAsync();
    }

    private static async IAsyncEnumerable<DataValue[]> Rows(int count, int textSize = 0)
    {
        for (var index = 1; index <= count; index++)
        {
            var text = "row " + index.ToString(CultureInfo.InvariantCulture);

            if (textSize > text.Length)
            {
                text = text.PadRight(textSize, 'x');
            }

            yield return
            [
                DataValue.FromText(ELogicalType.Integer32, index.ToString(CultureInfo.InvariantCulture)),
                DataValue.FromText(ELogicalType.String, text),
            ];
        }

        await Task.CompletedTask;
    }
}
