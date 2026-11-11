using System.Globalization;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DbAdapter.Definitions.Interchange;

using DatabaseInterchange.Format;
using DatabaseInterchange.Report;
using DatabaseInterchange.Script;
using DatabaseInterchange.Tests.Script;
using DatabaseInterchange.Transform;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// D4 and D4.2 at the exporter: the report runs after <c>manifest.json</c> closes and on both return
/// paths, and the cancellation boundary is that closing — not the report. A user who presses Cancel while
/// the summary is being written already holds a finished catalog, so the result has to say so with its
/// real counters instead of the message a failure at that point would produce.
/// </summary>
public sealed class SnapshotExporterCatalogReportTests : IDisposable
{
    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeExportReport_" + Guid.NewGuid().ToString("N"));

    /// <summary>The frames of the last export, in the order the exporter reported them.</summary>
    private readonly List<InterchangeProgress> _frames = [];

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_WithData_WritesTheReportIntoTheCatalogRoot_AndNamesItInTheResult()
    {
        // Act — the default options ask for the report, and the catalog is written by the real writer.
        var result = await ExportAsync(new ExportOptions { MaxRowsPerChunk = 2 });

        // Assert (AC1, FR15) — the path travels relative to the catalog directory, and the file is there.
        result.Success.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        result.ReportRelativePath.Should().Be(CatalogReportWriter.ReportFileName);
        ReadReport().Should().StartWith($"# Catalog summary — {ReportModel.Database}");

        // Assert — the report sits beside a catalog the verifier still accepts, so the artifact a restore
        // reads is unchanged by the text next to it (FR14).
        var verification = await new CatalogVerifier().VerifyAsync(_catalogPath);
        verification.IsValid.Should().BeTrue(string.Join("; ", verification.Issues));
    }

    [Fact]
    public async Task ExportAsync_SchemaOnly_RunsTheSameReportHelper_AndSaysNoDataWasCaptured()
    {
        // Act — the second return path: the exporter skips the data loop entirely.
        var result = await ExportAsync(new ExportOptions { IncludeData = false });

        // Assert (D4) — the schema-only store gets its report from the same call, not a parallel one.
        result.ReportRelativePath.Should().Be(CatalogReportWriter.ReportFileName);
        File.Exists(ReportPath()).Should().BeTrue();
        result.RowCount.Should().Be(0);
        result.TotalChunks.Should().Be(0);

        // Assert (FR10) — the text says the store held no data rather than that the tables were empty.
        ReadReport().Should().Contain("no data captured");
    }

    [Fact]
    public async Task ExportAsync_WhenTheOptionIsOff_WritesNoReport_AndReportsNoPath()
    {
        // Act
        var result = await ExportAsync(
            new ExportOptions { IncludeCatalogReport = false, MaxRowsPerChunk = 2 });

        // Assert (D4) — the catalog is complete and identical; only the summary is absent.
        result.Success.Should().BeTrue();
        result.ReportRelativePath.Should().BeNull();
        File.Exists(ReportPath()).Should().BeFalse();
        result.Issues.Should().BeEmpty("an option the user turned off is not a problem to report");

        // Assert — the stage that would have announced the write never appears on the bar.
        _frames.Should().NotContain(frame => frame.Stage == "Writing summary");
        result.RowCount.Should().Be(5, "the counters describe the catalog, which was written either way");
    }

    [Fact]
    public async Task ExportAsync_WhenCancelledBeforeTheManifestCloses_LeavesTheStoreIncomplete()
    {
        // Arrange — a cancel landing inside the data loop, which is before the catalog was finished.
        var act = () => ExportAsync(new ExportOptions { MaxRowsPerChunk = 2 }, cancelAtStage: "Writing data");

        // Assert — the cancellation propagates; the exporter does not swallow a cancel that has real work left.
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName))
            .Should().BeFalse("the manifest is what closes the catalog, and this run never reached it");
        File.Exists(ReportPath()).Should().BeFalse("an incomplete catalog has nothing to summarise");
    }

    [Fact]
    public async Task ExportAsync_WhenCancelledDuringTheSummaryStage_StopsTheReportAndKeepsTheCatalogUsable()
    {
        // Act — the cancel lands on the <c>Writing summary / 96</c> frame, after the manifest closed.
        var result = await ExportAsync(new ExportOptions { MaxRowsPerChunk = 2 }, cancelAtStage: "Writing summary");

        // Assert (D4.2) — the store is finished: success, its real counters, no issue, and a catalog the
        // verifier accepts. Telling the user "catalog not usable" here would be a lie about a usable file.
        result.Success.Should().BeTrue();
        result.Issues.Should().BeEmpty("a stage the user stopped is not a failure the catalog made");
        result.RowCount.Should().Be(5);
        result.TotalChunks.Should().Be(ChunkFilesOnDisk(), "the counters are the catalog's own, not the stopped stage's");
        result.CatalogBytes.Should().BeGreaterThan(0);
        result.ReportRelativePath.Should().BeNull("the report is what was stopped");
        File.Exists(ReportPath()).Should().BeFalse();
        File.Exists(Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName)).Should().BeTrue();

        // Assert — the folder still restores: verification is the gate the importer applies.
        var verification = await new CatalogVerifier().VerifyAsync(_catalogPath);
        verification.IsValid.Should().BeTrue(string.Join("; ", verification.Issues));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportAsync_ReportsMonotoneFrames_ClosingWithCompleteOnce(bool includeData)
    {
        // Act
        await ExportAsync(new ExportOptions { IncludeData = includeData, MaxRowsPerChunk = 2 });

        // Assert (FR12, D4.2) — the bar never runs backwards and reaches its end exactly once.
        _frames.Select(frame => frame.Percentage).Should().BeInAscendingOrder(
            "a stage that lands out of order reads as a stalled operation");
        _frames[^1].Stage.Should().Be("Complete");
        _frames[^1].Percentage.Should().Be(100);
        _frames.Take(_frames.Count - 1).Should().NotContain(
            frame => frame.Percentage == 100, "nothing is finished before the last frame");

        // Assert — the summary is announced exactly once and nothing follows it but the close.
        var summaryIndex = _frames.FindIndex(frame => frame.Stage == "Writing summary");
        summaryIndex.Should().BeGreaterThanOrEqualTo(0, "the report stage announces itself");
        _frames.Count(frame => frame.Stage == "Writing summary").Should().Be(1);
        _frames[summaryIndex + 1].Stage.Should().Be("Complete", "no frame is reported after the summary");
        _frames.Should().HaveCount(summaryIndex + 2);
    }

    [Fact]
    public async Task ExportAsync_WithTheScriptOption_WritesBothFiles_AndStillReportsTheSummary()
    {
        // Act
        var result = await ExportAsync(new ExportOptions { MaxRowsPerChunk = 2, IncludeSqlScripts = true });

        // Assert (FR1, FR7) — the store names what it wrote instead of leaving the user to open the folder.
        result.Success.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        result.ReportRelativePath.Should().Be(CatalogReportWriter.ReportFileName);
        result.SqlScripts.Should().NotBeNull();
        result.SqlScripts!.Succeeded.Should().BeTrue();
        result.SqlScripts.SchemaPath.Should().Be(CatalogScriptWriter.SchemaRelativePath);
        result.SqlScripts.DataPath.Should().Be(CatalogScriptWriter.DataRelativePath);
        result.SqlScripts.SchemaBytes.Should().BeGreaterThan(0);
        result.SqlScripts.SchemaStatements.Should().BeGreaterThan(0);
        result.SqlScripts.Rows.Should().Be(5, "every row the catalog captured is written into the data file");
        File.Exists(ScriptPath(CatalogScriptWriter.SchemaFileName)).Should().BeTrue();
        File.Exists(ScriptPath(CatalogScriptWriter.DataFileName)).Should().BeTrue();

        // Assert (D9) — the summary describes the files rather than counting them into itself.
        ReadReport().Should().Contain("## SQL scripts");

        // Assert — the scripts sit beside a catalog the verifier still accepts (FR14, D10).
        var verification = await new CatalogVerifier().VerifyAsync(_catalogPath);
        verification.IsValid.Should().BeTrue(string.Join("; ", verification.Issues));
    }

    [Fact]
    public async Task ExportAsync_WithTheOptionOff_WritesNeitherScript_AndSaysNothingAboutThem()
    {
        // Act — the product default: a caller that asks for nothing gets today's catalog (FR10).
        var result = await ExportAsync(new ExportOptions { MaxRowsPerChunk = 2 });

        // Assert — the pass never ran, so there is neither a file nor a section nor a stage.
        result.Success.Should().BeTrue();
        result.SqlScripts.Should().BeNull();
        File.Exists(ScriptPath(CatalogScriptWriter.SchemaFileName)).Should().BeFalse();
        File.Exists(ScriptPath(CatalogScriptWriter.DataFileName)).Should().BeFalse();
        ReadReport().Should().NotContain("## SQL scripts");
        _frames.Should().NotContain(frame => frame.Stage == "Writing SQL scripts");
    }

    [Fact]
    public async Task ExportAsync_TheScriptsAreNotPartOfTheCatalogsOwnMeasures()
    {
        // Arrange — the same source and the same options but for the scripts, into the same folder.
        var withoutScripts = await ExportAsync(new ExportOptions { MaxRowsPerChunk = 2 });

        Directory.Delete(_catalogPath, recursive: true);
        _frames.Clear();

        // Act
        var withScripts = await ExportAsync(new ExportOptions { MaxRowsPerChunk = 2, IncludeSqlScripts = true });

        // Assert (FR9, D10) — a file that describes the catalog does not change what the catalog is: the
        // three measures are the writer's own facts, taken while the catalog was being written.
        withScripts.Success.Should().BeTrue();
        withScripts.CatalogBytes.Should().Be(withoutScripts.CatalogBytes);
        withScripts.DataBytes.Should().Be(withoutScripts.DataBytes);
        withScripts.MetadataBytes.Should().Be(withoutScripts.MetadataBytes);
    }

    [Fact]
    public async Task ExportAsync_WhenTheRendererSpeaksAnotherDialect_CompletesTheStore_AndWarns()
    {
        // Arrange — a catalog whose recorded dialect this installation cannot write (D11).
        var act = () => ExportAsync(
            new ExportOptions { MaxRowsPerChunk = 2, IncludeSqlScripts = true },
            scriptDialect: "mysql");

        // Assert (FR8) — the refusal is one warning and no file, and the catalog is what it would have been.
        var result = await act.Should().NotThrowAsync();
        result.Subject.Success.Should().BeTrue();
        result.Subject.SqlScripts!.Succeeded.Should().BeFalse();
        result.Subject.SqlScripts.Refusal!.Code.Should().Be(CatalogScriptWriter.RefusalCode);
        result.Subject.Issues.Should().ContainSingle(issue =>
            issue.Code == CatalogScriptWriter.RefusalCode && issue.Severity == EIssueSeverity.Warning);
        Directory.Exists(Path.Combine(_catalogPath, CatalogScriptWriter.DirectoryName)).Should().BeFalse(
            "a pass that refused before writing leaves no folder behind");

        var verification = await new CatalogVerifier().VerifyAsync(_catalogPath);
        verification.IsValid.Should().BeTrue(string.Join("; ", verification.Issues));
    }

    [Fact]
    public async Task ExportAsync_WhenTheScriptRoleCannotBeBuilt_CompletesTheStore_AndWarns()
    {
        // Arrange — an adapter whose script role cannot even be constructed: the failure happens outside the
        // writer's own handling, and the store has already succeeded by then (FR8).
        var act = () => ExportAsync(
            new ExportOptions { MaxRowsPerChunk = 2, IncludeSqlScripts = true },
            rendererFactory: () => throw new NotSupportedException("This adapter writes no script."));

        // Assert — one warning, no file, and a catalog that is exactly what the store would have written.
        var result = await act.Should().NotThrowAsync();
        result.Subject.Success.Should().BeTrue();
        result.Subject.SqlScripts!.Succeeded.Should().BeFalse();
        result.Subject.SqlScripts.Refusal!.Code.Should().Be(CatalogScriptWriter.WriteFailureCode);
        result.Subject.Issues.Should().ContainSingle(issue =>
            issue.Code == CatalogScriptWriter.WriteFailureCode && issue.Severity == EIssueSeverity.Warning);
        Directory.Exists(Path.Combine(_catalogPath, CatalogScriptWriter.DirectoryName)).Should().BeFalse();

        var verification = await new CatalogVerifier().VerifyAsync(_catalogPath);
        verification.IsValid.Should().BeTrue(string.Join("; ", verification.Issues));
    }

    [Fact]
    public async Task ExportAsync_WithTheScripts_AnnouncesThemBeforeTheTotalsThatMeasureTheFolder()
    {
        // Act
        await ExportAsync(new ExportOptions { MaxRowsPerChunk = 2, IncludeSqlScripts = true });

        // Assert (D8, FR12) — the pass finishes before the totals are read, so what the summary measures is
        // the folder as it stands, and the bar only moves forward.
        _frames.Select(frame => frame.Percentage).Should().BeInAscendingOrder();

        var completing = _frames.FindIndex(frame => frame.Stage == "Completing");
        var scripts = _frames.FindIndex(frame => frame.Stage == "Writing SQL scripts");
        var summary = _frames.FindIndex(frame => frame.Stage == "Writing summary");

        completing.Should().BeGreaterThanOrEqualTo(0);
        scripts.Should().BeGreaterThan(completing, "the scripts are a pass after the catalog is closed");
        _frames[scripts].Percentage.Should().Be(96);
        summary.Should().BeGreaterThan(scripts, "the summary describes a folder the script pass has finished with");
        _frames[summary].Percentage.Should().Be(97);
    }

    private async Task<ExportResult> ExportAsync(
        ExportOptions options,
        string? cancelAtStage = null,
        string scriptDialect = "postgresql",
        Func<ISqlScriptRenderer>? rendererFactory = null)
    {
        using var source = new CancellationTokenSource();

        // The adapter is the product's boundary: the exporter only ever sees a dialect, a provider name
        // and a reader, so no engine and no connection is involved in what these tests decide.
        var adapter = Substitute.For<IDatabaseAdapter>();
        adapter.Info.Returns(new AdapterInfo("org.test.adapter", "postgresql", "*", "1.0.0"));
        adapter.ProviderName.Returns("Test Engine");
        adapter.OpenReader(Arg.Any<DatabaseTarget>(), Arg.Any<ExportOptions>()).Returns(new StubReader());

        // The script role answers the same way the PostgreSQL one would: one statement per object, in the
        // dialect the catalog records, so a mismatch is the only thing a test has to arrange. The factory is
        // invoked per call, which is what lets a test fail the role's construction itself.
        adapter.CreateScriptRenderer().Returns(
            _ => rendererFactory is null ? new RecordingScriptRenderer(scriptDialect) : rendererFactory());

        _frames.Clear();
        var exporter = new SnapshotExporter(adapter, NullLogger<SnapshotExporter>.Instance);

        return await exporter.ExportAsync(
            new ExportRequest
            {
                Source = new DatabaseTarget(
                    "postgresql",
                    "Host=test.example.com;Database=testdb;Username=test_user;Password=fake_pw_123"),
                CatalogDirectory = _catalogPath,
                Options = options,
            },
            new StageCancellingProgress(_frames, source, cancelAtStage),
            source.Token);
    }

    private string ReportPath() => Path.Combine(_catalogPath, CatalogReportWriter.ReportFileName);

    /// <summary>One of the two files the script pass writes, at the path the catalog folder gives it.</summary>
    private string ScriptPath(string fileName) =>
        Path.Combine(_catalogPath, CatalogScriptWriter.DirectoryName, fileName);

    /// <summary>The chunk files the writer actually landed in the folder.</summary>
    private int ChunkFilesOnDisk() =>
        Directory.EnumerateFiles(_catalogPath, "data.*.csv", SearchOption.AllDirectories).Count();

    private string ReadReport() => File.ReadAllText(ReportPath());

    /// <summary>
    /// Records every frame and cancels the operation from inside the one named by
    /// <paramref name="cancelAtStage"/>, which is how a test lands a cancellation at an exact point of the
    /// walk instead of racing a timer against it.
    /// </summary>
    private sealed class StageCancellingProgress(
        List<InterchangeProgress> frames,
        CancellationTokenSource source,
        string? cancelAtStage) : IProgress<InterchangeProgress>
    {
        public void Report(InterchangeProgress value)
        {
            frames.Add(value);

            if (cancelAtStage is not null && value.Stage == cancelAtStage)
            {
                source.Cancel();
            }
        }
    }

    /// <summary>
    /// The source the exporter reads: two small tables, five rows between them and one foreign key, written
    /// to a temp catalog. No connection is opened, per the hermetic rule of §7.
    /// </summary>
    private sealed class StubReader : IDatabaseReader
    {
        public IDatabaseAdapter Adapter { get; } = Substitute.For<IDatabaseAdapter>();

        public ICapabilityDetector CapabilityDetector => null!;

        public Task<SnapshotSchema> ReadSchemaAsync(CancellationToken ct) =>
            Task.FromResult(new SnapshotSchema
            {
                Database = new DatabaseInfo { Name = ReportModel.Database },
                Schemas = [ReportModel.Schema("public")],
                Tables = [Customers(), Orders()],
            });

        public IAsyncEnumerable<DataValue[]> ReadTableRowsAsync(
            TableRef table, ERowOrder order, CancellationToken ct) =>
            table.Name == "customers" ? Rows(2) : Rows(3);

        public Task<IReadOnlyList<NativeObjectDefinition>> ReadNativeObjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NativeObjectDefinition>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TableDefinition Customers() => ReportModel.KeyedTable("public", "customers");

        private static TableDefinition Orders() =>
            ReportModel.Table(
                "public",
                "orders",
                ReportModel.Column("public", "orders", "id", nullable: false),
                ReportModel.Column("public", "orders", "customer_id", nullable: false)) with
            {
                PrimaryKey = ReportModel.PrimaryKey("public", "orders", "id"),
                ForeignKeys =
                [
                    ReportModel.Reference(
                        "public", "orders", "fk_orders_customer",
                        "public", "customers", ["customer_id"], ["id"]),
                ],
            };

        private static async IAsyncEnumerable<DataValue[]> Rows(int count)
        {
            for (var index = 1; index <= count; index++)
            {
                yield return new DataValue[]
                {
                    DataValue.FromText(ELogicalType.Integer32, index.ToString(CultureInfo.InvariantCulture)),
                    DataValue.FromText(ELogicalType.String, $"row {index}"),
                };
            }

            await Task.CompletedTask;
        }
    }
}
