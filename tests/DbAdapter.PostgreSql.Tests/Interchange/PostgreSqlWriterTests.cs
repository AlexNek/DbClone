using CanonicalModel.Interchange;

using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Interchange;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Models = CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The writer's decisions that need no database: what counts as a target that is not empty, which
/// stages it can build statements for, and what it does with a stage it cannot serve.
/// <para>
/// Running DDL against PostgreSQL is deliberately not covered here — opening a real connection and
/// asserting a schema appeared is an integration test, not a unit one. What a unit can prove is the
/// judgement around it: that an emptiness check asks the metadata role the whole plugin already
/// uses, that a stage with nothing in it is not reported as a failure, and that a stage this
/// adapter cannot serve refuses instead of letting the import claim objects it never created.
/// </para>
/// </summary>
public class PostgreSqlWriterTests
{
    private readonly IDatabaseAdapter _adapter = Substitute.For<IDatabaseAdapter>();

    private readonly IMetadataSource _metadata = Substitute.For<IMetadataSource>();

    private bool _rowBatchPulled;

    public PostgreSqlWriterTests() =>
        _adapter.Info.Returns(new AdapterInfo("org.postgresql.adapter", "postgresql", "*", "1.0.0"));

    [Fact]
    public async Task EnsureEmptyTargetAsync_TargetHoldingUserTables_IsRefused()
    {
        Given(InterchangeTestModel.Empty() with { Tables = [Table("orders")] });
        await using var writer = CreateWriter();

        var act = () => writer.EnsureEmptyTargetAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(act);

        error.Message.Should().Contain("testdb").And.Contain("1 table");
    }

    [Fact]
    public async Task EnsureEmptyTargetAsync_TargetHoldingOnlyWhatEveryDatabaseHas_IsAccepted()
    {
        Given(
            InterchangeTestModel.Empty() with
            {
                Schemas = [new Models.SchemaDefinition("public", "test_user")],
                Extensions = [new Models.ExtensionDefinition("plpgsql", "pg_catalog", "1.0", null)],
            });
        await using var writer = CreateWriter();

        await writer.Invoking(w => w.EnsureEmptyTargetAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureEmptyTargetAsync_AsksTheMetadataRoleToExcludePlatformSchemas()
    {
        Given(InterchangeTestModel.Empty());
        await using var writer = CreateWriter();

        await writer.EnsureEmptyTargetAsync(CancellationToken.None);

        await _metadata.Received(1).ReadDatabaseModelAsync(
            excludePlatformSchemas: true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DescribeUserContent_TargetHoldingNothing_NamesNothing() =>
        PostgreSqlWriter.DescribeUserContent(InterchangeTestModel.Empty())
            .Should().BeEmpty();

    [Fact]
    public void DescribeUserContent_SystemSchema_IsNotUserContent()
    {
        var model = InterchangeTestModel.Empty() with
        {
            Schemas =
            [
                new Models.SchemaDefinition("public", "test_user"),
                new Models.SchemaDefinition("pg_catalog", "test_user", IsSystem: true),
            ],
        };

        PostgreSqlWriter.DescribeUserContent(model).Should().BeEmpty();
    }

    [Fact]
    public void DescribeUserContent_NamesEachKindWithItsOwnCount()
    {
        var model = InterchangeTestModel.Empty() with
        {
            Sequences = [Sequence("orders_id_seq")],
            Enums =
            [
                new Models.EnumDefinition("public", "mood", ["sad", "happy"], null),
                new Models.EnumDefinition("public", "tier", ["low", "high"], null),
            ],
            Extensions =
            [
                new Models.ExtensionDefinition("postgis", "public", "3.4.0", null),
                new Models.ExtensionDefinition("pg_trgm", "public", "1.6", null),
            ],
        };

        PostgreSqlWriter.DescribeUserContent(model)
            .Should()
            .Equal("1 sequence", "2 enum types", "2 extensions");
    }

    [Fact]
    public async Task BuildStatements_Schemas_WritesTheSameStatementTheCopyPipelineUses()
    {
        var objects = new ISchemaObject[]
        {
            new SchemaDefinition { Name = "sales" },
            new SchemaDefinition { Name = "Sales Report" },
        };
        await using var writer = CreateWriter();

        writer.BuildStatements(SchemaStage.Schemas, objects, ObjectNameMap.Empty)
            .Should()
            .Equal(
                "CREATE SCHEMA IF NOT EXISTS sales;",
                "CREATE SCHEMA IF NOT EXISTS \"Sales Report\";");
    }

    [Fact]
    public async Task BuildStatements_Schemas_GivenAnotherKindOfObject_IsRefused()
    {
        await using var writer = CreateWriter();

        var act = () =>
            writer.BuildStatements(
                SchemaStage.Schemas,
                [new SequenceDefinition { Name = "orders_id_seq" }],
                ObjectNameMap.Empty);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("SequenceDefinition");
    }

    [Fact]
    public async Task BuildStatements_Sequences_PlacesEachSequenceInTheSchemaTheCatalogNames()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new SequenceDefinition { Name = "order_seq", SchemaId = SalesId },
        };

        writer.BuildStatements(SchemaStage.Sequences, objects, NamesOneSchema())
            .Should()
            .Equal(
                "CREATE SEQUENCE sales.order_seq AS bigint INCREMENT BY 1 START WITH 1 "
                + "NO MINVALUE NO MAXVALUE CACHE 1;");
    }

    [Fact]
    public async Task BuildStatements_ParentTheReaderLeftUnnamed_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new SequenceDefinition { Name = "order_seq", SchemaId = OtherSchemaId },
        };

        var act = () => writer.BuildStatements(SchemaStage.Sequences, objects, NamesOneSchema());

        // The default schema is the one answer that must never be the fallback: it would place the
        // sequence somewhere the source never said it lived.
        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("order_seq");
    }

    [Fact]
    public async Task BuildStatements_Views_WritesTheStatementsTheCopyPipelineUses()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new ViewDefinition
            {
                Name = "order_total",
                SchemaId = SalesId,
                Definition = new DatabaseExpression("postgresql", "SELECT 1"),
            },
            new ViewDefinition
            {
                Name = "order_totals",
                SchemaId = SalesId,
                Definition = new DatabaseExpression("postgresql", "SELECT 2"),
                IsMaterialized = true,
            },
        };

        writer.BuildStatements(SchemaStage.Views, objects, NamesOneSchema())
            .Should()
            .Equal(
                $"CREATE OR REPLACE VIEW sales.order_total AS{Environment.NewLine}SELECT 1;",
                $"CREATE MATERIALIZED VIEW sales.order_totals AS{Environment.NewLine}SELECT 2 WITH NO DATA;");
    }

    [Fact]
    public async Task BuildStatements_TextFromAnotherDialect_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new ViewDefinition
            {
                Name = "order_total",
                SchemaId = SalesId,
                Definition = new DatabaseExpression("mysql", "SELECT 1"),
            },
        };

        var act = () => writer.BuildStatements(SchemaStage.Views, objects, NamesOneSchema());

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("mysql").And.Contain("postgresql");
    }

    [Fact]
    public async Task BuildStatements_Routines_RunsTheCapturedStatementAsItStands()
    {
        await using var writer = CreateWriter();
        const string Statement = "CREATE FUNCTION sales.f() RETURNS int LANGUAGE sql AS 'SELECT 1'";
        var objects = new ISchemaObject[]
        {
            new RoutineDefinition
            {
                Name = "f",
                SchemaId = SalesId,
                NativeDefinition = new NativeDefinition("postgresql", Statement),
            },
        };

        writer.BuildStatements(SchemaStage.Routines, objects, NamesOneSchema())
            .Should()
            .Equal($"{Statement};");
    }

    [Fact]
    public async Task BuildStatements_RoutineWithoutCapturedBody_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new RoutineDefinition { Name = "f", SchemaId = SalesId },
        };

        var act = () => writer.BuildStatements(SchemaStage.Routines, objects, NamesOneSchema());

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("carries no captured body");
    }

    [Theory]
    [InlineData(SchemaStage.Types)]
    [InlineData(SchemaStage.Tables)]
    [InlineData(SchemaStage.Constraints)]
    [InlineData(SchemaStage.Indexes)]
    [InlineData(SchemaStage.Triggers)]
    [InlineData(SchemaStage.PostData)]
    public async Task BuildStatements_AStageThisAdapterCannotServe_Refuses(SchemaStage stage)
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[] { new SchemaDefinition { Name = "sales" } };

        var act = () => writer.BuildStatements(stage, objects, NamesOneSchema());

        act.Should().Throw<NotSupportedException>().Which.Message
            .Should().Contain(stage.ToString());
    }

    [Fact]
    public async Task ApplySchemaAsync_StageHoldingNothing_IsNotReportedAsAFailure()
    {
        await using var writer = CreateWriter();

        // Nothing of that kind is in the catalog — that is a fact about the source, not a limit of
        // the adapter, and it must not be the reason an import stops.
        await writer.Invoking(
                w => w.ApplySchemaAsync(
                    SchemaStage.Tables,
                    [],
                    ObjectNameMap.Empty,
                    CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteTableRowsAsync_DoesNotReadTheBatchItCannotApply()
    {
        await using var writer = CreateWriter();

        var act = () => writer.WriteTableRowsAsync(
            new TableRef("public", "orders"),
            ReadRowBatch(),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<NotSupportedException>(act);

        error.Message.Should().Contain("orders");
        _rowBatchPulled.Should().BeFalse(
            "reading rows the writer cannot place would leave the target short and the report green");
    }

    [Fact]
    public async Task ApplySecurityAsync_StageHoldingObjects_Refuses()
    {
        await using var writer = CreateWriter();
        var security = new SecuritySection
        {
            Roles = [new RoleDefinition { Name = "test_reader" }],
        };

        var act = () => writer.ApplySecurityAsync(
            ESecurityStage.Principals,
            security,
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<NotSupportedException>(act);

        error.Message.Should().Contain("Principals");
    }

    [Fact]
    public async Task ApplySecurityAsync_SectionHoldingNothing_IsNotReportedAsAFailure()
    {
        await using var writer = CreateWriter();

        await writer.Invoking(
                w => w.ApplySecurityAsync(
                    ESecurityStage.Grants,
                    new SecuritySection(),
                    CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_DoesNotReportPassWithoutLooking()
    {
        await using var writer = CreateWriter();

        var act = () => writer.ValidateAsync(new TargetExpectation(), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Writer_AfterDispose_RefusesEveryRole()
    {
        Given(InterchangeTestModel.Empty());
        var writer = CreateWriter();
        await writer.DisposeAsync();

        await writer.Invoking(w => w.EnsureEmptyTargetAsync(CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
        await writer.Invoking(
                w => w.ApplySchemaAsync(
                    SchemaStage.Schemas,
                    [],
                    ObjectNameMap.Empty,
                    CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
        await writer.Invoking(w => w.ValidateAsync(new TargetExpectation(), CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// The schema id the map below answers. A staged object names its parent by id alone, so a test
    /// that wants an object placed must name the id the same way the catalog reader did.
    /// </summary>
    private static readonly InterchangeId SalesId =
        InterchangeId.Compute("schema", string.Empty, "sales");

    private static readonly InterchangeId OtherSchemaId =
        InterchangeId.Compute("schema", string.Empty, "unnamed");

    private static ObjectNameMap NamesOneSchema() =>
        new(
            new Dictionary<InterchangeId, string> { [SalesId] = "sales" },
            new Dictionary<InterchangeId, TableRef>());

    private static Models.SequenceDefinition Sequence(string name) =>
        new("public", name, 1, 1, null, null, 1, false, "bigint", null);

    private static Models.TableDefinition Table(string name) =>
        new(
            SchemaName: "public",
            Name: name,
            Columns: [],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: null);

    private void Given(Models.DatabaseModel model) =>
        _metadata
            .ReadDatabaseModelAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(model);

    private PostgreSqlWriter CreateWriter() =>
        new(
            _adapter,
            TestTargets.Local(),
            new ImportOptions(),
            NullLogger<PostgreSqlWriter>.Instance,
            _metadata);

    private async IAsyncEnumerable<DataValue[]> ReadRowBatch()
    {
        _rowBatchPulled = true;
        await Task.CompletedTask;
        yield return [];
    }
}
