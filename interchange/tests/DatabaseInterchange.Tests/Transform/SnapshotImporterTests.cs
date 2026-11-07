using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;
using DatabaseInterchange.Transform;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace DatabaseInterchange.Tests.Transform;

/// <summary>
/// Hermetic tests for the import pass: a catalog is written to a temp directory and imported into a
/// writer that records what it was asked to do. No connection is opened.
/// <para>
/// What the importer owns — and therefore what only it can get wrong — is the order the stages are
/// applied in, and the names behind the ids. A catalog object points at its parent by
/// <see cref="InterchangeId"/> alone, and an adapter may not open a catalog to look an id up, so
/// resolving those ids into names is work the reader side has to finish before the objects cross the
/// boundary.
/// </para>
/// </summary>
public sealed class SnapshotImporterTests : IDisposable
{
    private static readonly InterchangeId PublicId =
        InterchangeId.Compute("schema", string.Empty, "public");

    private static readonly InterchangeId ReportingId =
        InterchangeId.Compute("schema", string.Empty, "reporting");

    private static readonly InterchangeId CustomersId =
        InterchangeId.Compute("table", "public", "customers", PublicId);

    private static readonly InterchangeId CustomersSeqId =
        InterchangeId.Compute("sequence", "reporting", "customers_seq", ReportingId);

    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeImportTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_AppliesEveryStageInTheOrderTheContractDeclares()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // SchemaStage's order is a dependency order, and routines come before views because a view
        // may call a function.
        writer.Stages.Should().Equal(
            SchemaStage.Schemas,
            SchemaStage.Sequences,
            SchemaStage.Tables,
            SchemaStage.Routines,
            SchemaStage.Views);
    }

    [Fact]
    public async Task ImportAsync_HandsTheWriterTheNamesBehindTheIds()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        var sequence = (SequenceDefinition)writer.Objects[SchemaStage.Sequences].Single();
        sequence.Name.Should().Be("customers_seq");

        // The object carries no schema name, only its id — so a writer that was never given the map
        // could not say where the sequence belongs.
        writer.Names[SchemaStage.Sequences].SchemaName(sequence.SchemaId).Should().Be("reporting");
    }

    [Fact]
    public async Task ImportAsync_AddressesRowsByTheSchemaTheirTableLivesIn()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // A bare table name would land the rows in whichever schema the connection's search path
        // reached first, so the schema has to travel with the table.
        writer.RowTargets.Should().Equal(new TableRef("public", "customers"));
    }

    [Fact]
    public async Task ImportAsync_TableWhoseSchemaTheCatalogDoesNotName_StopsBeforeReadingRows()
    {
        // Arrange
        await WriteCatalog(schemalessTable: true);
        var writer = new RecordingWriter();

        // Act
        var act = () => Import(writer);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*customers*");
        writer.RowTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_AppliesAStageEvenWhenTheCatalogHoldsNoneOfThatKind()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // An empty stage is passed through rather than skipped: what emptiness means is the adapter's
        // decision, not the importer's.
        writer.Objects[SchemaStage.Views].Should().BeEmpty();
    }

    private static async Task WriteCatalog(
        string catalogPath,
        bool schemalessTable = false)
    {
        var schemas = new List<SchemaDefinition>
        {
            new() { Id = PublicId, Name = "public" },
            new() { Id = ReportingId, Name = "reporting" },
        };

        var tables = new List<TableDefinition>
        {
            new()
            {
                Id = CustomersId,
                Name = "customers",
                SchemaId = schemalessTable
                    ? InterchangeId.Compute("schema", string.Empty, "not_captured")
                    : PublicId,
            },
        };

        var sequences = new List<SequenceDefinition>
        {
            new() { Id = CustomersSeqId, Name = "customers_seq", SchemaId = ReportingId },
        };

        await using var catalog = await CatalogWriter.CreateAsync(
            catalogPath,
            producer: "DbClone-test",
            sourceDialect: "postgresql");

        await catalog.WriteSectionAsync("schemas", schemas);
        await catalog.WriteSectionAsync("tables", tables);
        await catalog.WriteSectionAsync("sequences", sequences);
        await catalog.CompleteAsync();
    }

    private Task WriteCatalog(bool schemalessTable = false) =>
        WriteCatalog(_catalogPath, schemalessTable);

    private async Task<ImportResult> Import(RecordingWriter writer)
    {
        var adapter = Substitute.For<IDatabaseAdapter>();
        adapter.OpenWriter(Arg.Any<DatabaseTarget>(), Arg.Any<ImportOptions>()).Returns(writer);

        var importer = new SnapshotImporter(adapter, NullLogger<SnapshotImporter>.Instance);

        return await importer.ImportAsync(new ImportRequest
        {
            CatalogDirectory = _catalogPath,
            Target = new DatabaseTarget(
                "postgresql",
                "Host=test.example.com;Database=testdb;Username=test_user;Password=fake_pw_123"),
        });
    }

    /// <summary>
    /// A writer that records the calls instead of executing them and never fails, so a test sees the
    /// importer's decisions rather than a database's.
    /// </summary>
    private sealed class RecordingWriter : IDatabaseWriter
    {
        public List<SchemaStage> Stages { get; } = [];

        public Dictionary<SchemaStage, IReadOnlyList<ISchemaObject>> Objects { get; } = [];

        public Dictionary<SchemaStage, ObjectNameMap> Names { get; } = [];

        public List<TableRef> RowTargets { get; } = [];

        public IDatabaseAdapter Adapter { get; } = Substitute.For<IDatabaseAdapter>();

        public ICapabilityDetector CapabilityDetector => null!;

        public Task EnsureEmptyTargetAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ApplySchemaAsync(
            SchemaStage stage,
            IReadOnlyList<ISchemaObject> objects,
            ObjectNameMap names,
            CancellationToken ct)
        {
            Stages.Add(stage);
            Objects[stage] = objects;
            Names[stage] = names;

            return Task.CompletedTask;
        }

        public Task WriteTableRowsAsync(
            TableRef table,
            IAsyncEnumerable<DataValue[]> rows,
            CancellationToken ct)
        {
            // The batch is deliberately not enumerated: pulling rows is the writer's business, and
            // what this test cares about is the address the batch was handed with.
            RowTargets.Add(table);

            return Task.CompletedTask;
        }

        public Task ApplySecurityAsync(
            ESecurityStage stage,
            SecuritySection security,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<ImportValidationReport> ValidateAsync(
            TargetExpectation expectation,
            CancellationToken ct) =>
            Task.FromResult(new ImportValidationReport());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
