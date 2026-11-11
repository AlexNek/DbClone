using System.IO;
using System.Runtime.CompilerServices;

using CanonicalModel.DTOs;
using CanonicalModel.Interchange;

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
/// The store's SQL scripts, seen from the application side: the preference reaches the product's own
/// option, the result names both files with the sizes the product measured, and a refusal arrives as one
/// warning issue with no path rather than as an exception (FR8, FR10, FR13).
/// </summary>
/// <remarks>
/// Hermetic per §7: the source is a substituted adapter over an in-memory schema and the renderer is a
/// double, so no test here opens a database or speaks a dialect. The catalog is a real temp folder,
/// because the sizes asserted are the ones the pass measured on disk.
/// </remarks>
public sealed class InterchangeStoreSqlScriptTests : IDisposable
{
    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(), "DbCloneStoreScripts_" + Guid.NewGuid().ToString("N"));

    private readonly IDatabaseAdapter _adapter = Substitute.For<IDatabaseAdapter>();

    /// <summary>Gets the product options the last store was handed, as the wrapper built them.</summary>
    private ExportOptions? _lastOptions;

    private readonly InterchangeService _sut;

    public InterchangeStoreSqlScriptTests()
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

    [Fact]
    public async Task TheAppPreferenceBecomesTheProductOption()
    {
        // Act
        await StoreAsync(includeSqlScripts: true, renderer: new StubRenderer());

        // Assert (FR10) — without this line the checkbox would die in the settings file: the pass is
        // opt-in at the product boundary and only the app preference turns it on.
        _lastOptions.Should().NotBeNull("the store opens a reader, and the options travel with it");
        _lastOptions!.IncludeSqlScripts.Should().BeTrue();
    }

    [Fact]
    public async Task TheResultNamesBothFiles_WithTheSizesThePassMeasured()
    {
        // Act
        var result = await StoreAsync(includeSqlScripts: true, renderer: new StubRenderer());

        // Assert — both paths are absolute and inside the catalog the user chose, and both files are
        // there: the app never learns what the catalog calls them, only where they are (FR13).
        result.Outcome.Should().Be(EStoreOutcome.Success);
        result.Issues.Should().BeEmpty();

        var scripts = result.SqlScripts;
        scripts.Should().NotBeNull();
        scripts!.Written.Should().BeTrue();
        Path.GetDirectoryName(scripts.SchemaPath).Should().Be(Path.Combine(_catalogPath, "sql"));
        Path.GetDirectoryName(scripts.DataPath).Should().Be(Path.Combine(_catalogPath, "sql"));
        File.Exists(scripts.SchemaPath).Should().BeTrue();
        File.Exists(scripts.DataPath).Should().BeTrue();
        scripts.SchemaBytes.Should().Be(new FileInfo(scripts.SchemaPath!).Length);
        scripts.DataBytes.Should().Be(new FileInfo(scripts.DataPath!).Length);
        scripts.SchemaStatements.Should().BeGreaterThan(0);
        scripts.DataStatements.Should().BeGreaterThan(0);
        scripts.Rows.Should().Be(4, "two rows in each of the two tables the source captured");
    }

    [Fact]
    public async Task WithTheOptionOff_ThereIsNoPathAndNoFile()
    {
        // Act — the app-level default, which is how every other store in the app is built.
        var result = await StoreAsync(includeSqlScripts: false, renderer: new StubRenderer());

        // Assert (FR10) — off means off: no record to report, no folder, and no issue about it.
        result.Outcome.Should().Be(EStoreOutcome.Success);
        result.Issues.Should().BeEmpty("an option the user turned off is not a problem to report");
        result.SqlScripts.Should().BeNull();
        Directory.Exists(Path.Combine(_catalogPath, "sql")).Should().BeFalse();
    }

    [Fact]
    public async Task ARefusalIsOneWarningAndNoPath_NeverAnException()
    {
        // Act — a renderer whose dialect is not the one the catalog records (D11).
        var act = () => StoreAsync(includeSqlScripts: true, renderer: new StubRenderer("mysql"));

        // Assert (FR8) — the store still completes: the catalog is what it would have been, the reason
        // travels as one warning, and the summary written afterwards states it.
        var result = await act.Should().NotThrowAsync();
        result.Subject.Outcome.Should().Be(EStoreOutcome.CompletedWithIssues);
        result.Subject.IsComplete.Should().BeTrue();
        result.Subject.Issues.Should().ContainSingle()
            .Which.Severity.Should().Be(EInterchangeSeverity.Warning);

        result.Subject.SqlScripts.Should().NotBeNull();
        result.Subject.SqlScripts!.Written.Should().BeFalse();
        result.Subject.SqlScripts.SchemaPath.Should().BeNull();
        result.Subject.SqlScripts.DataPath.Should().BeNull();

        result.Subject.SummaryPath.Should().NotBeNull("the summary still describes a catalog that was finished");
        File.Exists(result.Subject.SummaryPath).Should().BeTrue();
    }

    /// <summary>Runs one store over the temp folder with the given app-level options.</summary>
    private async Task<InterchangeStoreResult> StoreAsync(bool includeSqlScripts, ISqlScriptRenderer renderer)
    {
        _adapter.OpenReader(Arg.Any<DatabaseTarget>(), Arg.Any<ExportOptions>())
            .Returns(call =>
            {
                _lastOptions = call.Arg<ExportOptions>();
                return new StubSource();
            });

        _adapter.CreateScriptRenderer().Returns(renderer);

        return await _sut.StoreAsync(
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
                VerifyAfterStore: true,
                IncludeSummaryReport: true,
                IncludeSqlScripts: includeSqlScripts),
            progress: null,
            CancellationToken.None);
    }

    /// <summary>
    /// Two small related tables, two rows each: enough for the data file to hold one block per table and
    /// for the counts the result carries to be non-zero.
    /// </summary>
    private sealed class StubSource : IDatabaseReader
    {
        public IDatabaseAdapter Adapter { get; } = Substitute.For<IDatabaseAdapter>();

        public ICapabilityDetector CapabilityDetector => null!;

        public Task<SnapshotSchema> ReadSchemaAsync(CancellationToken ct) =>
            Task.FromResult(new SnapshotSchema
            {
                Database = new DatabaseInfo { Name = "testdb" },
                Schemas = [new SchemaDefinition { Ref = ObjectRef.Schema("public"), Name = "public" }],
                Tables = [Table("customers"), Table("orders")],
            });

        public IAsyncEnumerable<DataValue[]> ReadTableRowsAsync(
            TableRef table, ERowOrder order, CancellationToken ct) =>
            Rows(2);

        public Task<IReadOnlyList<NativeObjectDefinition>> ReadNativeObjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NativeObjectDefinition>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TableDefinition Table(string name) =>
            new()
            {
                Ref = ObjectRef.Table("public", name),
                Name = name,
                SchemaRef = ObjectRef.Schema("public"),
                Columns =
                [
                    new ColumnDefinition
                    {
                        Ref = ObjectRef.Column("public", name, "id"),
                        Name = "id",
                        TableRef = ObjectRef.Table("public", name),
                        Ordinal = 1,
                        Nullable = false,
                        NativeType = new NativeType("integer"),
                    },
                    new ColumnDefinition
                    {
                        Ref = ObjectRef.Column("public", name, "note"),
                        Name = "note",
                        TableRef = ObjectRef.Table("public", name),
                        Ordinal = 2,
                        NativeType = new NativeType("text"),
                    },
                ],
                PrimaryKey = new PrimaryKeyDefinition
                {
                    Ref = ObjectRef.SubObject("constraint", "public", name, $"{name}_pkey"),
                    Name = $"{name}_pkey",
                    TableRef = ObjectRef.Table("public", name),
                    Columns = ["id"],
                },
            };

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

    /// <summary>
    /// One obvious statement per object in the dialect it claims: the layout, the ordering and the files
    /// belong to the product and are asserted in its own suite, so what this double makes visible is only
    /// that the application asked for the text and reported where it landed.
    /// </summary>
    private sealed class StubRenderer(string dialect = "postgresql") : ISqlScriptRenderer
    {
        public string Dialect { get; } = dialect;

        public IReadOnlyList<string> HeaderStatements() => ["SET TIME ZONE 'UTC';"];

        public IReadOnlyList<string> SchemaStatements(SchemaStage stage, IReadOnlyList<ISchemaObject> objects) =>
            [.. objects.Select(target => $"CREATE {stage} {target.Name};")];

        public async IAsyncEnumerable<string> InsertStatements(
            TableDefinition table,
            IAsyncEnumerable<DataValue[]> rows,
            long rowCount,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var seen = 0L;

            await foreach (var _ in rows.WithCancellation(ct))
            {
                seen++;
            }

            yield return $"INSERT INTO {table.Name} ({seen} row(s));";
        }

        public IReadOnlyList<string> TailStatements(
            IReadOnlyList<TableDefinition> tables,
            IReadOnlyList<ViewDefinition> views) =>
            ["VALIDATE ALL KEYS;"];
    }
}
