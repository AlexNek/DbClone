using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;
using DatabaseInterchange.Report;
using DatabaseInterchange.Transform;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// FR14: Restore never reads <c>summary.md</c>. The file is a courtesy text beside a catalog, so the read
/// model a restore is planned from must not move when the report is deleted, replaced by garbage, or left
/// as an orphan scratch file in the root — and no part of the read path may enumerate the root to decide
/// what to import. That structural fact is what these tests decide without opening a database.
/// </summary>
public sealed class CatalogReportInertToRestoreTests : IDisposable
{
    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeReportInert_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreFacingReads_AreUnchanged_WhenTheReportIsDeleted()
    {
        // Arrange — a catalog with its summary written by the real writer.
        await CreateCatalogAsync();
        await WriteReportAsync();
        var withReport = await ReadRestoreModelAsync();

        // Act
        File.Delete(ReportPath());

        // Assert (AC2) — verification, plan, every section's objects, the row stream and the manifest's own
        // list are exactly what they were while the file was present.
        var withoutReport = await ReadRestoreModelAsync();
        withoutReport.Should().BeEquivalentTo(withReport, "the report is not part of what a restore reads");
    }

    [Fact]
    public async Task RestoreFacingReads_AreUnchanged_WhenTheReportHoldsGarbage()
    {
        // Arrange
        await CreateCatalogAsync();
        await WriteReportAsync();
        var expected = await ReadRestoreModelAsync();

        // Act — text that is not a report at all, at the name the report uses.
        await File.WriteAllTextAsync(ReportPath(), "not a report");

        // Assert (FR14) — the verifier still accepts the catalog: the file carries no hash the manifest
        // records, so nothing checks its content and nothing can fail on it.
        var verification = await new CatalogVerifier().VerifyAsync(_catalogPath);
        verification.IsValid.Should().BeTrue(string.Join("; ", verification.Issues));
        (await ReadRestoreModelAsync()).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task RestoreFacingReads_AreUnchanged_ByAnOrphanScratchFileInTheRoot()
    {
        // Arrange
        await CreateCatalogAsync();
        var expected = await ReadRestoreModelAsync();

        // Act — the file an interrupted store leaves behind (D4.1.3): as inert as the report it was making.
        await File.WriteAllTextAsync(
            Path.Combine(_catalogPath, CatalogReportWriter.TempFileName), "half a report at most");

        // Assert
        var verification = await new CatalogVerifier().VerifyAsync(_catalogPath);
        verification.IsValid.Should().BeTrue(string.Join("; ", verification.Issues));
        (await ReadRestoreModelAsync()).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Manifest_NeverListsTheReport_SoNothingOnTheImportSideCanOpenIt()
    {
        // Arrange
        await CreateCatalogAsync();
        await WriteReportAsync();

        // Assert — the manifest is the catalog's content identity and the report is deliberately outside it:
        // no entry, so no hash, no size and no reason for a reader to look (D8).
        var manifestText = await File.ReadAllTextAsync(
            Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName));
        manifestText.Should().NotContain("summary", "the report is not a catalog file");

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Manifest.Files.Select(entry => entry.Path)
            .Should().NotContain(path => path.Contains("summary", StringComparison.Ordinal));
        reader.Manifest.Sections.Should().NotContain(section => section.Contains("summary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportPlanner_PlanIsIdentical_WithTheReportPresentAndAbsent()
    {
        // Arrange
        await CreateCatalogAsync();
        await WriteReportAsync();
        var withReport = await PlanAsync();

        // Act
        File.Delete(ReportPath());

        // Assert — the stages and their counts come from the sections, never from what the root holds.
        var withoutReport = await PlanAsync();
        withoutReport.Should().BeEquivalentTo(withReport);
        withoutReport.Stages.Should().NotBeEmpty("the plan is the shape a restore would run");
    }

    /// <summary>
    /// Everything the restore path reads out of a catalog: AC2's comparison surface. Built by the real
    /// reader, verifier and planner, so a change in any of them shows up here.
    /// </summary>
    private sealed record ReadModel
    {
        public CatalogVerificationResult Verification { get; init; } = new();

        public ImportPlan Plan { get; init; } = new();

        public ManifestFile Manifest { get; init; } = new();

        public IReadOnlyList<SchemaDefinition> Schemas { get; init; } = [];

        public IReadOnlyList<TableDefinition> Tables { get; init; } = [];

        public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

        public IReadOnlyList<ViewDefinition> Views { get; init; } = [];

        public IReadOnlyList<SequenceDefinition> Sequences { get; init; } = [];

        public SecuritySection? Security { get; init; }

        public IReadOnlyList<DataValue[]> Rows { get; init; } = [];

        public long? TableRows { get; init; }
    }

    private async Task<ReadModel> ReadRestoreModelAsync()
    {
        await using var reader = await CatalogReader.OpenAsync(_catalogPath);

        var rows = new List<DataValue[]>();
        await foreach (var row in reader.ReadRowsAsync("public", "customers"))
        {
            rows.Add(row);
        }

        return new ReadModel
        {
            Verification = await new CatalogVerifier().VerifyAsync(_catalogPath),
            Plan = await PlanAsync(),
            Manifest = reader.Manifest,
            Schemas = await reader.ReadSectionAsync<SchemaDefinition>("schemas"),
            Tables = await reader.ReadSectionAsync<TableDefinition>("tables"),
            Columns = await reader.ReadSectionAsync<ColumnDefinition>("columns"),
            Views = await reader.ReadSectionAsync<ViewDefinition>("views"),
            Sequences = await reader.ReadSectionAsync<SequenceDefinition>("sequences"),
            Security = await reader.ReadSecurityAsync(),
            Rows = rows,
            TableRows = await reader.ReadTableRowCountAsync("public", "customers"),
        };
    }

    private async Task<ImportPlan> PlanAsync()
    {
        // The planner reads the catalog and nothing else: no writer and no connection is involved, which is
        // what makes this the hermetic half of FR14.
        var planner = new ImportPlanner(
            Substitute.For<IDatabaseAdapter>(),
            NullLogger<ImportPlanner>.Instance);

        return await planner.PlanAsync(new ImportRequest
        {
            CatalogDirectory = _catalogPath,
            Target = new DatabaseTarget(
                "postgresql",
                "Host=test.example.com;Database=testdb;Username=test_user;Password=fake_pw_123"),
        });
    }

    /// <summary>
    /// Two related tables, both with rows: the verifier checks that every table the catalog index names has
    /// a data folder, so a fixture that leaves one unwritten would fail for a reason this test is not about.
    /// </summary>
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

    private async Task CreateCatalogAsync()
    {
        var snapshot = Fixture();

        await using var writer = await CatalogWriter.CreateAsync(
            _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql");
        await writer.WriteSectionAsync("schemas", [ReportModel.Schema("public")]);
        await writer.WriteSectionAsync("tables", snapshot.Tables);
        await writer.WriteSectionAsync("columns", [.. snapshot.Tables.SelectMany(t => t.Columns)]);
        foreach (var table in snapshot.Tables)
        {
            await writer.WriteTableAsync(
                table, "public", Rows(3), new ExportOptions { MaxRowsPerChunk = 2 });
        }

        await writer.CompleteAsync();
    }

    private async Task WriteReportAsync()
    {
        var snapshot = Fixture();
        await new CatalogReportWriter().WriteAsync(
            _catalogPath,
            ReportModel.Statistics(snapshot),
            ReportModel.MapOf(snapshot),
            new List<InterchangeIssue>(),
            CancellationToken.None);
    }

    private static async IAsyncEnumerable<DataValue[]> Rows(int count)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return new DataValue[]
            {
                DataValue.FromText(ELogicalType.Integer32, index.ToString()),
                DataValue.FromText(ELogicalType.String, $"row {index}"),
            };
        }

        await Task.CompletedTask;
    }

    private string ReportPath() => Path.Combine(_catalogPath, CatalogReportWriter.ReportFileName);
}
