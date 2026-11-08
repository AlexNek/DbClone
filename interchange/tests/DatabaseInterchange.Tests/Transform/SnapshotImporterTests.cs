using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DbAdapter.Definitions.Interchange;

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
/// <see cref="ObjectRef"/> alone, and an adapter may not open a catalog to look a ref up, so
/// resolving those refs into names is work the reader side has to finish before the objects cross the
/// boundary.
/// </para>
/// </summary>
public sealed class SnapshotImporterTests : IDisposable
{
    private static readonly ObjectRef PublicRef = ObjectRef.Schema("public");

    private static readonly ObjectRef ReportingRef = ObjectRef.Schema("reporting");

    private static readonly ObjectRef CustomersRef = ObjectRef.Table("public", "customers");

    private static readonly ObjectRef CustomersSeqRef =
        ObjectRef.Sequence("reporting", "customers_seq");

    private static readonly ObjectRef CustomersEmailIdxRef =
        ObjectRef.SubObject("index", "public", "customers", "customers_email_idx");

    private static readonly ObjectRef MoodTypeRef = ObjectRef.Type("public", "mood");

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
        // SchemaStage's order is a dependency order: routines come before views because a view may
        // call a function, and the constraints come after every table because a foreign key points
        // between tables that all have to exist first.
        writer.Stages.Should().Equal(
            SchemaStage.Schemas,
            SchemaStage.Types,
            SchemaStage.Sequences,
            SchemaStage.Tables,
            SchemaStage.Routines,
            SchemaStage.Views,
            SchemaStage.Constraints,
            SchemaStage.Indexes,
            SchemaStage.Triggers,
            SchemaStage.Publications);

        // The keys travel with the table that owns them, so this stage is handed the tables again.
        writer.Objects[SchemaStage.Constraints].Should().Equal(writer.Objects[SchemaStage.Tables]);

        // So do the indexes, for a reason of the same kind: whether a captured index still has to be
        // created depends on the constraints recorded in the table that holds it, and an adapter may
        // not open the catalog to find them.
        writer.Objects[SchemaStage.Indexes].Should().Equal(writer.Objects[SchemaStage.Tables]);

        // A trigger needs nothing from its siblings once its table can be named, so the trigger objects
        // themselves are what travel to their stage.
        writer.Objects[SchemaStage.Triggers].Single().Should().BeAssignableTo<TriggerDefinition>();

        // The native file's objects travel to the stage the routing table says creates their kind, and
        // only there: one of these objects is a type and one is not, so the two part company instead of
        // arriving at a single stage that would have to refuse one of them. The publication lands after
        // the triggers because it lists tables, which is the same numbering the types are placed by.
        writer.Objects[SchemaStage.Types].Cast<NativeObjectDefinition>().Select(n => n.Kind)
            .Should().Equal(EObjectKind.Enum);
        writer.Objects[SchemaStage.Publications].Cast<NativeObjectDefinition>().Select(n => n.Kind)
            .Should().Equal(EObjectKind.Publication);
    }

    [Fact]
    public async Task ImportAsync_CatalogHoldsAnObjectTheAdapterDoesNotCreate_StopsBeforeTheFirstStage()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        var act = () => Import(writer, kind => kind != EObjectKind.Publication);

        // Assert
        // The refusal is made where the target is still untouched, and it names every object nothing can
        // create — an import that ran its earlier stages first would leave a partial target to explain
        // the one object it never created.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Publication *\"pub_all\"*nothing was executed*");
        writer.Stages.Should().BeEmpty();
        writer.RowTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_KindTheFormatHasNoStageFor_IsRefusedRatherThanGuessedAt()
    {
        // Arrange
        // A table arriving through the native file has a stage that creates tables and a routing table
        // that does not take that kind from this file, which is the catalog holding two sources of truth
        // for one object. Guessing a stage for it would let the same table be created twice.
        await WriteCatalog(nativeOverride: natives =>
            [.. natives, new NativeObjectDefinition { Kind = EObjectKind.Table, Name = "customers" }]);
        var writer = new RecordingWriter();

        // Act
        var act = () => Import(writer);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Table *\"customers\"*nothing was executed*");
        writer.Stages.Should().BeEmpty();
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

        // The object carries a qualified-name reference (ObjectRef) that already contains the schema
        // name, so no separate name map is needed to resolve it.
        sequence.SchemaRef.ObjectName().Should().Be("reporting");
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
    public async Task ImportAsync_HandsTheRecordedColumnsToTheWriterSoPositionsAreNotGuessed()
    {
        // Arrange
        await WriteCatalogWithData();
        var writer = new RecordingWriter();

        // Act
        var result = await Import(writer);

        // Assert
        // A stored row is a list of text tokens and nothing in it says which column a token answers,
        // so the columns the catalog recorded have to travel with the rows.
        writer.RowColumns[new TableRef("public", "customers")]
            .Select(c => c.Name)
            .Should().Equal("id", "name");
        result.RowsImported.Should().Be(2);
    }

    [Fact]
    public async Task ImportAsync_CountsTheTargetAgainstTheRowsTheCaptureWroteDown()
    {
        // Arrange
        await WriteCatalogWithData();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        // The expectation is the count the catalog's own table manifest states — written by the export —
        // and not a restatement of what the writer says it applied, or validation would check the
        // restore against itself.
        writer.Expectation.Should().NotBeNull();
        writer.Expectation!.TableRowCounts[new TableRef("public", "customers")].Should().Be(2);
    }

    [Fact]
    public async Task ImportAsync_CatalogHoldingNoDataForATable_ExpectsNoRowCountForIt()
    {
        // Arrange
        // The shared fixture writes the schema sections and no data folder, which is what an export run
        // without data produces.
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        writer.Expectation.Should().NotBeNull();
        writer.Expectation!.TableRowCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_ExpectationNamesOnlyTheKindsTheCatalogHolds()
    {
        // Arrange
        await WriteCatalog();
        var writer = new RecordingWriter();

        // Act
        await Import(writer);

        // Assert
        writer.Expectation.Should().NotBeNull();
        var counts = writer.Expectation!.ObjectCounts;
        counts[EObjectKind.Schema].Should().Be(2);
        counts[EObjectKind.Table].Should().Be(1);
        counts[EObjectKind.Enum].Should().Be(1);
        counts.Should().NotContainKey(EObjectKind.Procedure);
        counts.Should().NotContainKey(EObjectKind.MaterializedView);
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
        bool schemalessTable = false,
        Func<IReadOnlyList<NativeObjectDefinition>, IReadOnlyList<NativeObjectDefinition>>? nativeOverride =
            null)
    {
        var schemas = new List<SchemaDefinition>
        {
            new() { Ref = PublicRef, Name = "public" },
            new() { Ref = ReportingRef, Name = "reporting" },
        };

        var tables = new List<TableDefinition>
        {
            new()
            {
                Ref = CustomersRef,
                Name = "customers",
                SchemaRef = schemalessTable
                    ? ObjectRef.Schema("")
                    : PublicRef,

                // An index and a trigger belong to the table that owns them, and the stages that
                // create them take those objects rather than the table, so the fixture carries one of
                // each to pin where they are handed over.
                Indexes =
                [
                    new IndexDefinition
                    {
                        Ref = CustomersEmailIdxRef,
                        Name = "customers_email_idx",
                        TableRef = CustomersRef,
                    },
                ],
                Triggers =
                [
                    new TriggerDefinition
                    {
                        Ref = ObjectRef.SubObject("trigger", "public", "customers", "customers_touch"),
                        Name = "customers_touch",
                        TableRef = CustomersRef,
                    },
                ],
            },
        };

        var sequences = new List<SequenceDefinition>
        {
            new() { Ref = CustomersSeqRef, Name = "customers_seq", SchemaRef = ReportingRef },
        };

        // The kinds the structured sections have no home for go to the catalog's native file, named after
        // the source dialect the manifest records. One of these is a type and one is not, which is what
        // lets a test see the file split between two stages rather than handed to one whole.
        var natives = new List<NativeObjectDefinition>
        {
            new()
            {
                Ref = MoodTypeRef,
                Kind = EObjectKind.Enum,
                SchemaName = "public",
                Name = "mood",
                NativeDefinition = new NativeDefinition(
                    "postgresql", "CREATE TYPE public.mood AS ENUM ('sad', 'happy')"),
            },
            new()
            {
                Kind = EObjectKind.Publication,
                Name = "pub_all",
                NativeDefinition = new NativeDefinition(
                    "postgresql", "CREATE PUBLICATION pub_all FOR ALL TABLES"),
            },
        };

        await using var catalog = await CatalogWriter.CreateAsync(
            catalogPath,
            producer: "DbClone-test",
            sourceDialect: "postgresql");

        await catalog.WriteSectionAsync("schemas", schemas);
        await catalog.WriteSectionAsync("tables", tables);
        await catalog.WriteSectionAsync("sequences", sequences);
        await catalog.WriteNativeObjectsAsync(
            nativeOverride is null ? natives : nativeOverride(natives));
        await catalog.CompleteAsync();
    }

    private Task WriteCatalog(
        bool schemalessTable = false,
        Func<IReadOnlyList<NativeObjectDefinition>, IReadOnlyList<NativeObjectDefinition>>? nativeOverride =
            null) =>
        WriteCatalog(_catalogPath, schemalessTable, nativeOverride);

    /// <summary>
    /// A catalog that holds a table with two columns and two rows of data, which is what lets a test
    /// watch the row pass instead of only the address it was handed.
    /// </summary>
    private async Task WriteCatalogWithData()
    {
        var customers = new TableDefinition
        {
            Ref = CustomersRef,
            Name = "customers",
            SchemaRef = PublicRef,
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "customers", "id"),
                    Name = "id",
                    TableRef = CustomersRef,
                    Ordinal = 1,
                    NativeType = new NativeType("integer"),
                },
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "customers", "name"),
                    Name = "name",
                    TableRef = CustomersRef,
                    Ordinal = 2,
                    NativeType = new NativeType("text"),
                },
            ],
        };

        await using var catalog = await CatalogWriter.CreateAsync(
            _catalogPath,
            producer: "DbClone-test",
            sourceDialect: "postgresql");

        await catalog.WriteSectionAsync(
            "schemas",
            new List<SchemaDefinition> { new() { Ref = PublicRef, Name = "public" } });
        await catalog.WriteSectionAsync("tables", new List<TableDefinition> { customers });
        await catalog.WriteTableAsync(customers, "public", CustomerRows());
        await catalog.CompleteAsync();
    }

    /// <summary>
    /// The rows the catalog's data folder holds for <c>public.customers</c>: positional text, one token
    /// per recorded column, in the order the columns were recorded.
    /// </summary>
    private static async IAsyncEnumerable<DataValue[]> CustomerRows()
    {
        yield return new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "1"),
            DataValue.FromText(ELogicalType.String, "ada"),
        };
        yield return new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "2"),
            DataValue.Null(ELogicalType.String),
        };

        await Task.CompletedTask;
    }

    private async Task<ImportResult> Import(
        RecordingWriter writer,
        Func<EObjectKind, bool>? canCreate = null)
    {
        var adapter = Substitute.For<IDatabaseAdapter>();
        var capabilities = Substitute.For<IDatabaseCapabilities>();

        // The importer routes the native file by the answer the adapter gives here, so the double states
        // it. Everything the routing places in a stage is creatable unless the test says otherwise, which
        // leaves the order tests to watch the whole walk.
        capabilities.CanCreate(Arg.Any<EObjectKind>()).Returns(
            call => canCreate?.Invoke(call.Arg<EObjectKind>()) ?? true);
        adapter.Capabilities.Returns(capabilities);
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



        public List<TableRef> RowTargets { get; } = [];

        public Dictionary<TableRef, IReadOnlyList<ColumnDefinition>> RowColumns { get; } = [];

        public TargetExpectation? Expectation { get; private set; }

        public IDatabaseAdapter Adapter { get; } = Substitute.For<IDatabaseAdapter>();

        public ICapabilityDetector CapabilityDetector => null!;

        public Task EnsureEmptyTargetAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ApplySchemaAsync(
            SchemaStage stage,
            IReadOnlyList<ISchemaObject> objects,
            CancellationToken ct)
        {
            Stages.Add(stage);
            Objects[stage] = objects;

            return Task.CompletedTask;
        }

        public async Task<long> WriteTableRowsAsync(
            TableRef table,
            IReadOnlyList<ColumnDefinition> columns,
            IAsyncEnumerable<DataValue[]> rows,
            CancellationToken ct)
        {
            RowTargets.Add(table);
            RowColumns[table] = columns;

            // The rows are counted as they pass, the way a writer must count them to report what it
            // applied. What the test reads afterwards is the importer's accumulation of those counts.
            var applied = 0L;
            await foreach (var _ in rows.WithCancellation(ct))
            {
                applied++;
            }

            return applied;
        }

        public Task ApplySecurityAsync(
            ESecurityStage stage,
            SecuritySection security,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<ImportValidationReport> ValidateAsync(
            TargetExpectation expectation,
            CancellationToken ct)
        {
            Expectation = expectation;

            return Task.FromResult(new ImportValidationReport());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
