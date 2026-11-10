using System.IO;

using CanonicalModel.DTOs;
using CanonicalModel.Interchange;

using DatabaseInterchange.Format;

using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;

using DbClone.Application.Adapters;
using DbClone.Application.DTOs;
using DbClone.Application.Interchange;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

namespace Application.Tests.Interchange;

/// <summary>
/// D5's mapping, seen from the application side: the user's preference reaches the product's own option,
/// the writer's real numbers replace the placeholders this wrapper used to print, and the report's path is
/// made absolute in the one place that knows the catalog directory. The app still names no catalog file.
/// </summary>
/// <remarks>
/// Hermetic per §7: the source is a substituted adapter over an in-memory <see cref="SnapshotSchema"/>, so
/// no database is opened. The catalog is a real temp folder, because every number asserted here is the
/// number the writer measured on disk.
/// </remarks>
public sealed class InterchangeStoreReportTests : IDisposable
{
    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(), "DbCloneStoreReport_" + Guid.NewGuid().ToString("N"));

    private readonly IDatabaseAdapter _adapter = Substitute.For<IDatabaseAdapter>();

    /// <summary>Gets the product options the last store was handed, as the wrapper built them.</summary>
    private ExportOptions? _lastOptions;

    private readonly InterchangeService _sut;

    public InterchangeStoreReportTests()
    {
        var syntax = Substitute.For<IConnectionStringSyntax>();
        syntax.BuildKeyValue(Arg.Any<ConnectionStringFields>())
            .Returns("Host=test.example.com;Port=5432;Database=testdb;Username=test_user");

        _adapter.Info.Returns(new AdapterInfo("org.test.adapter", "postgresql", "*", "1.0.0"));
        _adapter.ProviderName.Returns("Test Engine");
        _adapter.Syntax.Returns(syntax);

        var catalog = Substitute.For<IAdapterCatalog>();
        catalog.All.Returns(new[] { _adapter });
        catalog.Find(Arg.Any<string>()).Returns(_adapter);

        _sut = new InterchangeService(
            new DatabaseTargetFactory(catalog),
            Substitute.For<ILogger<InterchangeService>>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAppPreferenceBecomesTheProductOption(bool includeSummaryReport)
    {
        // Arrange
        GiveSource(rowsPerTable: 2);

        // Act
        await StoreAsync(includeSummaryReport: includeSummaryReport);

        // Assert — FR11's wiring: the setting is an app preference, and the wrapper is what translates its
        // name into the product's option. Without this line the checkbox would die in the settings file.
        _lastOptions.Should().NotBeNull("the store opens a reader, and the options travel with it");
        _lastOptions!.IncludeCatalogReport.Should().Be(includeSummaryReport);
    }

    [Fact]
    public async Task SummaryPathIsInsideTheCatalog_WhenTheReportWasWritten()
    {
        // Arrange
        GiveSource(rowsPerTable: 2);

        // Act
        var result = await StoreAsync(includeSummaryReport: true);

        // Assert — the product hands back a name relative to the catalog it was given, so the wrapper's
        // job is to combine it. The app never learns what that file is called.
        result.Outcome.Should().Be(EStoreOutcome.Success);
        result.SummaryPath.Should().NotBeNull();
        Path.GetDirectoryName(result.SummaryPath).Should().Be(_catalogPath);
        File.Exists(result.SummaryPath).Should().BeTrue("the app must not advertise a file that is not there");
    }

    [Fact]
    public async Task NoSummaryPathAndNoReportFile_WhenTheOptionIsOff()
    {
        // Arrange
        GiveSource(rowsPerTable: 2);

        // Act
        var result = await StoreAsync(includeSummaryReport: false);

        // Assert — off means off: no path to show the user, and nothing in the folder to show it.
        result.SummaryPath.Should().BeNull();
        ReportFiles().Should().BeEmpty();
        result.Outcome.Should().Be(EStoreOutcome.Success);
        result.Issues.Should().BeEmpty("an option the user turned off is not a problem to report");
    }

    [Fact]
    public async Task BytesAndChunks_AreTheMeasuresTheWriterReached()
    {
        // Arrange — three rows per table over two tables, so every counter is non-trivial.
        GiveSource(rowsPerTable: 3);

        // Act
        var result = await StoreAsync(includeSummaryReport: true);

        // Assert (D4, AC10) — the fields that used to be filled with zeros now carry the writer's own
        // totals, measured against the same folder.
        result.RowsExported.Should().Be(6);
        result.ChunksWritten.Should().Be(ChunkFiles(), "the chunk count is what landed in the folder");

        // Assert — BytesExported is the catalog-artifact measure: every file except the report, which is
        // strictly more than the row data alone.
        result.BytesExported.Should().Be(EveryFileExceptReport());
        result.BytesExported.Should().BeGreaterThan(DataBytes(), "it is the artifact, not the data");
    }

    [Fact]
    public async Task SectionsWritten_CountsTheSectionsTheWriterActuallyWrote()
    {
        // Arrange — the same schema twice, the second time with security content. The catalog then carries
        // one section more, which a hard-coded count could not follow. Each store's own index is read
        // before the next one replaces the folder.
        GiveSource(rowsPerTable: 2);
        var plain = await StoreAsync();
        var plainNames = await SectionNamesInCatalogAsync();

        GiveSource(rowsPerTable: 2, withSecurity: true);
        var secured = await StoreAsync();
        var securedNames = await SectionNamesInCatalogAsync();

        // Assert — the writer's own record of what it landed.
        plain.SectionsWritten.Should().Be(
            plainNames.Count, $"the writer recorded: {string.Join(", ", plainNames)}");
        secured.SectionsWritten.Should().Be(
            securedNames.Count, $"the writer recorded: {string.Join(", ", securedNames)}");

        // Assert — and the number moves with the catalog, which the literal it replaced never did.
        secured.SectionsWritten.Should().Be(plain.SectionsWritten + 1);
    }

    [Fact]
    public async Task AFailedReportWrite_LeavesTheStoreComplete_WithOneWarningIssue()
    {
        // Arrange — the report's own name is occupied by a directory, created while the data is still being
        // written, so the final move cannot land. This is FR13's read-only-folder case without a folder
        // that would also break the store itself.
        GiveSource(rowsPerTable: 2, blockReportPath: true);

        // Act
        var result = await StoreAsync(verifyAfterStore: false);

        // Assert — the catalog is the product and the report a courtesy: complete, with one warning.
        result.Outcome.Should().Be(EStoreOutcome.CompletedWithIssues);
        result.IsComplete.Should().BeTrue("a manifest that closed describes a usable catalog");
        result.SummaryPath.Should().BeNull();
        result.RowsExported.Should().Be(4, "the data was written before the optional stage failed");

        // Assert — exactly one issue, a warning, and it is the report write.
        result.Issues.Should().ContainSingle()
            .Which.Severity.Should().Be(EInterchangeSeverity.Warning);
        result.Issues.Should().ContainSingle().Which.Code.Should().Be("CatalogReportNotWritten");
    }

    [Fact]
    public void BytesExportedIsDocumentedAsTheCatalogArtifact()
    {
        // Act — D5 replaces a placeholder with a real measure under a name that reads like raw data, so the
        // doc comment is part of the contract and is guarded like one.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "DbClone.Application", "Interchange", "InterchangeStoreResult.cs"));

        // Assert
        source.Should().Contain("catalog artifact in bytes");
        source.Should().Contain("Not the data alone");
    }

    /// <summary>Runs one store over the temp folder with the given app-level options.</summary>
    private async Task<InterchangeStoreResult> StoreAsync(
        bool includeSummaryReport = true, bool verifyAfterStore = true) =>
        await _sut.StoreAsync(
            new InterchangeStoreRequest(
                Connection: new ConnectionInfo(
                    "test.example.com", 5432, "testdb", "test_user", "fake_pw_123", "Prefer"),
                TableSelection: TableSelectionSpec.All,
                CatalogPath: _catalogPath,
                ChunkBytes: 8_388_608,
                RowsPerChunk: 50_000,
                IncludeData: true,
                SnapshotConsistency: "consistent",
                UnsupportedFeaturePolicy: "warn",
                VerifyAfterStore: verifyAfterStore,
                IncludeSummaryReport: includeSummaryReport),
            progress: null,
            CancellationToken.None);

    /// <summary>Points the substituted adapter at an in-memory source of the given shape.</summary>
    private void GiveSource(int rowsPerTable, bool withSecurity = false, bool blockReportPath = false) =>
        _adapter.OpenReader(Arg.Any<DatabaseTarget>(), Arg.Any<ExportOptions>())
            .Returns(call =>
            {
                _lastOptions = call.Arg<ExportOptions>();

                return new StubSource(_catalogPath, rowsPerTable, withSecurity, blockReportPath);
            });

    private int ChunkFiles() =>
        Directory.EnumerateFiles(_catalogPath, "data.*.csv", SearchOption.AllDirectories).Count();

    /// <summary>The section names the writer recorded in the catalog index it closed with.</summary>
    private async Task<IReadOnlyList<string>> SectionNamesInCatalogAsync()
    {
        await using var reader = await CatalogReader.OpenAsync(_catalogPath);

        return reader.Manifest.Sections;
    }

    private string[] ReportFiles() =>
        Directory.GetFiles(_catalogPath, "summary*", SearchOption.AllDirectories);

    /// <summary>Every byte in the folder except the report's own — the measure <c>CatalogBytes</c> states.</summary>
    private long EveryFileExceptReport() =>
        Directory.EnumerateFiles(_catalogPath, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetFileName(file).StartsWith("summary", StringComparison.Ordinal))
            .Sum(file => new FileInfo(file).Length);

    private long DataBytes() =>
        Directory.EnumerateFiles(_catalogPath, "data.*.csv", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DbClone.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("DbClone.slnx not found above the test assembly.");
    }

    /// <summary>
    /// The source the store reads: two related tables and a configurable amount of what the writer measures.
    /// <paramref name="blockReportPath"/> is the fault injection of the failed-write test — it puts a
    /// directory where the report's move wants to land, once the catalog folder already exists.
    /// </summary>
    private sealed class StubSource(
        string catalogPath,
        int rowsPerTable,
        bool withSecurity,
        bool blockReportPath) : IDatabaseReader
    {
        public IDatabaseAdapter Adapter { get; } = Substitute.For<IDatabaseAdapter>();

        public ICapabilityDetector CapabilityDetector => null!;

        public Task<SnapshotSchema> ReadSchemaAsync(CancellationToken ct) =>
            Task.FromResult(new SnapshotSchema
            {
                Database = new DatabaseInfo { Name = "testdb" },
                Schemas = [new SchemaDefinition { Ref = ObjectRef.Schema("public"), Name = "public" }],
                Tables = [Customers(), Orders()],
                Security = withSecurity
                    ? new SecuritySection { Roles = [new RoleDefinition { Name = "test_role" }] }
                    : null,
            });

        public IAsyncEnumerable<DataValue[]> ReadTableRowsAsync(
            TableRef table, ERowOrder order, CancellationToken ct)
        {
            if (blockReportPath)
            {
                Directory.CreateDirectory(Path.Combine(catalogPath, "summary.md"));
            }

            return Rows(rowsPerTable);
        }

        public Task<IReadOnlyList<NativeObjectDefinition>> ReadNativeObjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NativeObjectDefinition>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TableDefinition Customers() => Keyed("public", "customers", "id", "name");

        private static TableDefinition Orders() => Keyed("public", "orders", "id", "customer_id");

        private static TableDefinition Keyed(string schema, string table, string key, string other) =>
            new()
            {
                Ref = ObjectRef.Table(schema, table),
                Name = table,
                SchemaRef = ObjectRef.Schema(schema),
                Columns =
                [
                    Column(schema, table, key, nullable: false),
                    Column(schema, table, other),
                ],
                PrimaryKey = new PrimaryKeyDefinition
                {
                    Ref = ObjectRef.SubObject("constraint", schema, table, $"{table}_pkey"),
                    Name = $"{table}_pkey",
                    TableRef = ObjectRef.Table(schema, table),
                    Columns = [key],
                },
                ForeignKeys = table == "orders"
                    ?
                    [
                        new ForeignKeyDefinition
                        {
                            Ref = ObjectRef.SubObject("constraint", schema, table, "fk_orders_customer"),
                            Name = "fk_orders_customer",
                            TableRef = ObjectRef.Table(schema, table),
                            ReferencedTableRef = ObjectRef.Table(schema, "customers"),
                            Columns = ["customer_id"],
                            ReferencedColumns = ["id"],
                        },
                    ]
                    : [],
            };

        private static ColumnDefinition Column(
            string schema, string table, string name, bool nullable = true) =>
            new()
            {
                Ref = ObjectRef.Column(schema, table, name),
                Name = name,
                TableRef = ObjectRef.Table(schema, table),
                Ordinal = 1,
                Nullable = nullable,
                NativeType = new NativeType("integer"),
            };

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
    }
}
