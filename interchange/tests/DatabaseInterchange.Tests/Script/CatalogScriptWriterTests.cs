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
            _catalogPath, new RecordingScriptRenderer(), [], issues, CancellationToken.None);

        // Assert (FR1, FR9) — both files exist at the names the result states, the sizes are the files'
        // own lengths, and nothing was reported: the pass wrote what it was asked for.
        result.Succeeded.Should().BeTrue();
        result.SchemaPath.Should().Be(CatalogScriptWriter.SchemaRelativePath);
        result.DataPath.Should().Be(CatalogScriptWriter.DataRelativePath);
        File.Exists(SchemaFile()).Should().BeTrue();
        File.Exists(DataFile()).Should().BeTrue();
        result.SchemaBytes.Should().Be(new FileInfo(SchemaFile()).Length);
        result.DataBytes.Should().Be(new FileInfo(DataFile()).Length);
        result.SchemaBytes.Should().BeGreaterThan(0);
        result.SchemaStatements.Should().BeGreaterThan(0);
        result.DataStatements.Should().BeGreaterThan(0);
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
        await _sut.WriteAsync(_catalogPath, renderer, [], new List<InterchangeIssue>(), CancellationToken.None);

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
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), CancellationToken.None);

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
            _catalogPath, new RecordingScriptRenderer(), [], issues, CancellationToken.None);

        // Assert (D2) — with no second file, the schema file is the one written last and carries the tail.
        result.Succeeded.Should().BeTrue();
        result.DataPath.Should().BeNull();
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
        await _sut.WriteAsync(_catalogPath, renderer, [], new List<InterchangeIssue>(), CancellationToken.None);

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
            _catalogPath, renderer, [], new List<InterchangeIssue>(), CancellationToken.None);

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
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), CancellationToken.None);
        var previousSchema = await File.ReadAllBytesAsync(SchemaFile());
        var previousData = await File.ReadAllBytesAsync(DataFile());
        var issues = new List<InterchangeIssue>();

        ScriptResult result;
        using (var held = new FileStream(SchemaFile(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Act
            result = await _sut.WriteAsync(
                _catalogPath, new RecordingScriptRenderer(), [], issues, CancellationToken.None);
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
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), CancellationToken.None);
        var previousSchema = await File.ReadAllBytesAsync(SchemaFile());
        var previousData = await File.ReadAllBytesAsync(DataFile());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], issues, cts.Token);

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
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), CancellationToken.None);
        var previousSchema = await File.ReadAllBytesAsync(SchemaFile());
        var previousData = await File.ReadAllBytesAsync(DataFile());
        var issues = new List<InterchangeIssue>();

        // Act
        var result = await _sut.WriteAsync(
            _catalogPath,
            new RecordingScriptRenderer { UnscriptableRows = "orders" },
            [],
            issues,
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
            _catalogPath, new RecordingScriptRenderer(), [], issues, cts.Token);

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
            _catalogPath, new RecordingScriptRenderer(), [], issues, CancellationToken.None);

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
            _catalogPath, new RecordingScriptRenderer(), [], issues, CancellationToken.None);

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
            _catalogPath, new RecordingScriptRenderer { Unscriptable = "orders" }, [], issues, CancellationToken.None);

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
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), CancellationToken.None);
        var firstSchema = await File.ReadAllBytesAsync(SchemaFile());
        var firstData = await File.ReadAllBytesAsync(DataFile());
        await _sut.WriteAsync(
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), CancellationToken.None);

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
            _catalogPath, new RecordingScriptRenderer(), [], new List<InterchangeIssue>(), CancellationToken.None);

        // Assert (FR11) — a file a user commits or diffs must not carry a byte nobody asked for.
        foreach (var path in new[] { SchemaFile(), DataFile() })
        {
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[..3].Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "a BOM is not SQL");
            bytes.Should().NotContain((byte)'\r', "one line break, whatever the platform");
            bytes[^1].Should().Be((byte)'\n');
        }
    }

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

    /// <summary>
    /// A real catalog. Interchange format by default, because that is what the exporter writes and the only
    /// shape carrying the captured text a script is made of; <paramref name="directFormat"/> builds the other.
    /// </summary>
    private async Task CreateCatalogAsync(
        bool withRows = true,
        string dialect = Dialect,
        bool directFormat = false,
        InterchangeSnapshot? snapshot = null)
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
                table, "public", Rows(withRows ? 2 : 0), new ExportOptions { MaxRowsPerChunk = 2 });
        }

        await writer.CompleteAsync();
    }

    private static async IAsyncEnumerable<DataValue[]> Rows(int count)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return
            [
                DataValue.FromText(ELogicalType.Integer32, index.ToString()),
                DataValue.FromText(ELogicalType.String, $"row {index}"),
            ];
        }

        await Task.CompletedTask;
    }
}
