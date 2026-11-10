using System.Globalization;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DbAdapter.Definitions.Interchange;

using DatabaseInterchange.Format;
using DatabaseInterchange.Report;
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

    private async Task<ExportResult> ExportAsync(ExportOptions options, string? cancelAtStage = null)
    {
        using var source = new CancellationTokenSource();

        // The adapter is the product's boundary: the exporter only ever sees a dialect, a provider name
        // and a reader, so no engine and no connection is involved in what these tests decide.
        var adapter = Substitute.For<IDatabaseAdapter>();
        adapter.Info.Returns(new AdapterInfo("org.test.adapter", "postgresql", "*", "1.0.0"));
        adapter.ProviderName.Returns("Test Engine");
        adapter.OpenReader(Arg.Any<DatabaseTarget>(), Arg.Any<ExportOptions>()).Returns(new StubReader());

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
