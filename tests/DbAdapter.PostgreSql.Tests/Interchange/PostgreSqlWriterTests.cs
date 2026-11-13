using System.Text;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;
using DbAdapter.PostgreSql.Interchange;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;
using NpgsqlTypes;

using NSubstitute;

using Models = CanonicalModel.Models;
using Task = System.Threading.Tasks.Task;

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

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Schemas, objects)
            .Should()
            .Equal(
                "CREATE SCHEMA IF NOT EXISTS sales;",
                "CREATE SCHEMA IF NOT EXISTS \"Sales Report\";");
    }

    [Fact]
    public void BuildStatements_Schemas_GivenASystemSchema_CreatesNothingForIt()
    {
        // The server rejects the name itself (42939) whatever IF NOT EXISTS says, and every target
        // it bootstrapped already holds the schema — a catalog captured before the export stopped
        // recording them must still restore.
        var objects = new ISchemaObject[]
        {
            new SchemaDefinition { Name = "public" },
            new SchemaDefinition { Name = "information_schema" },
            new SchemaDefinition { Name = "pg_catalog" },
            new SchemaDefinition { Name = "PG_TOAST" },
        };

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Schemas, objects)
            .Should().Equal("CREATE SCHEMA IF NOT EXISTS public;");
    }

    [Fact]
    public async Task BuildStatements_Schemas_GivenAnotherKindOfObject_IsRefused()
    {
        await using var writer = CreateWriter();

        var act = () =>
            PostgreSqlWriter.BuildStatements("postgresql",
                SchemaStage.Schemas,
                [new SequenceDefinition { Name = "orders_id_seq" }]);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("SequenceDefinition");
    }

    [Fact]
    public async Task BuildStatements_Types_RunsEachRecordedStatementAndOrdersByKind()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            // Handed in the order a name-sorted file would list them, which is not the order they can
            // be created in: the domain rests on the composite, which holds the enum.
            Type(EObjectKind.Domain, "sales", "a_amount", "CREATE DOMAIN sales.a_amount AS numeric;"),
            Type(EObjectKind.CompositeType, "sales", "address", "CREATE TYPE sales.address AS (mood sales.mood)"),
            Type(EObjectKind.Enum, "sales", "mood", "CREATE TYPE sales.mood AS ENUM ('sad', 'happy')"),
        };

        var statements = PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Types, objects);

        // The semicolon the capture left on the first statement is not doubled.
        statements.Should().Equal(
            "CREATE TYPE sales.mood AS ENUM ('sad', 'happy');",
            "CREATE TYPE sales.address AS (mood sales.mood);",
            "CREATE DOMAIN sales.a_amount AS numeric;");
    }

    [Theory]
    [InlineData(EObjectKind.Publication)]
    [InlineData(EObjectKind.Subscription)]
    public async Task BuildStatements_Types_GivenAnObjectTheRoutingPlacesElsewhere_IsACallerError(
        EObjectKind kind)
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            Type(kind, string.Empty, "pub_all", "CREATE PUBLICATION pub_all FOR ALL TABLES"),
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Types, objects);

        // Whether a kind is created at all is the adapter's declared capability, answered before the
        // import writes anything, and which stage creates it is the routing table's answer. An object of
        // another kind reaching this stage is therefore a caller that misread those two — a programming
        // error, and reported as one rather than as a fact about the catalog.
        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain(kind.ToString());
    }

    [Fact]
    public async Task BuildStatements_Types_GivenAnotherKindOfObject_IsRefused()
    {
        await using var writer = CreateWriter();

        var act = () => PostgreSqlWriter.BuildStatements(
            "postgresql",
            SchemaStage.Types,
            [new SchemaDefinition { Name = "sales" }]);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain(nameof(NativeObjectDefinition));
    }

    [Fact]
    public async Task BuildStatements_TypeWithoutRecordedStatement_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new NativeObjectDefinition { Kind = EObjectKind.Enum, SchemaName = "sales", Name = "mood" },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Types, objects);

        // The format keeps no field for an enum's labels, so there is nothing here to write a statement
        // from — and inventing labels would create a type the source never had.
        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("no recorded statement");
    }

    [Fact]
    public async Task BuildStatements_TypeRecordedAsAFragment_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new NativeObjectDefinition
            {
                Kind = EObjectKind.Enum,
                SchemaName = "sales",
                Name = "mood",
                NativeDefinition = new NativeDefinition(
                    "postgresql", "CREATE TYPE sales.mood AS", Complete: false),
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Types, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("fragment");
    }

    [Fact]
    public async Task BuildStatements_TypeFromAnotherDialect_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new NativeObjectDefinition
            {
                Kind = EObjectKind.Enum,
                SchemaName = "sales",
                Name = "mood",
                NativeDefinition = new NativeDefinition(
                    "mysql", "CREATE TYPE sales.mood AS ENUM ('sad')"),
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Types, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("mysql").And.Contain("postgresql");
    }

    [Fact]
    public async Task BuildStatements_Sequences_PlacesEachSequenceInTheSchemaTheCatalogNames()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new SequenceDefinition { Name = "order_seq", SchemaRef = SalesSchemaRef },
        };

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Sequences, objects)
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
            new SequenceDefinition { Name = "order_seq", SchemaRef = ObjectRef.Schema("") },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Sequences, objects);

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
                SchemaRef = SalesSchemaRef,
                Definition = new DatabaseExpression("postgresql", "SELECT 1"),
            },
            new ViewDefinition
            {
                Name = "order_totals",
                SchemaRef = SalesSchemaRef,
                Definition = new DatabaseExpression("postgresql", "SELECT 2"),
                IsMaterialized = true,
            },
        };

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Views, objects)
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
                SchemaRef = SalesSchemaRef,
                Definition = new DatabaseExpression("mysql", "SELECT 1"),
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Views, objects);

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
                SchemaRef = SalesSchemaRef,
                NativeDefinition = new NativeDefinition("postgresql", Statement),
            },
        };

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Routines, objects)
            .Should()
            .Equal($"{Statement};");
    }

    [Fact]
    public async Task BuildStatements_RoutineWithoutCapturedBody_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new RoutineDefinition { Name = "f", SchemaRef = SalesSchemaRef },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Routines, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("carries no captured body");
    }

    [Fact]
    public async Task BuildStatements_Tables_WritesEveryFactTheCatalogKeepsIntoOneStatement()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                Columns =
                [
                    Column("id", "integer", nullable: false) with { Identity = "yes" },
                    Column("note", "text") with
                    {
                        Collation = "C",
                        DefaultValue = new DatabaseExpression("postgresql", "'n/a'::text"),
                    },
                    Column("total", "numeric(10,2)") with
                    {
                        Generated = new DatabaseExpression("postgresql", "price * 2"),
                    },
                ],
                PrimaryKey = new PrimaryKeyDefinition
                {
                    Name = "orders_pkey",
                    Columns = ["id"],
                },
                UniqueConstraints =
                [
                    new UniqueConstraintDefinition { Name = "orders_note_key", Columns = ["note"] },
                ],
                CheckConstraints =
                [
                    new CheckConstraintDefinition
                    {
                        Name = "orders_total_check",
                        Expression = new DatabaseExpression(
                            "postgresql",
                            "CHECK ((total > (0)::numeric)) NOT VALID"),
                    },
                ],
            },
        };

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, objects)
            .Should()
            .Equal(
                // The identity column keeps its NOT NULL, the collation and the generated expression
                // are written, and the recorded NOT VALID clause is dropped because CREATE TABLE
                // rejects it. The layout is the copy pipeline generator's: the first declaration
                // stays on the opening line.
                "CREATE TABLE sales.orders (id integer GENERATED BY DEFAULT AS IDENTITY NOT NULL,"
                + $"{Environment.NewLine}    note text COLLATE \"C\" DEFAULT 'n/a'::text,"
                + $"{Environment.NewLine}    total numeric(10,2) GENERATED ALWAYS AS (price * 2) STORED,"
                + $"{Environment.NewLine}    CONSTRAINT orders_pkey PRIMARY KEY (id),"
                + $"{Environment.NewLine}    CONSTRAINT orders_note_key UNIQUE (note),"
                + $"{Environment.NewLine}    CONSTRAINT orders_total_check CHECK ((total > (0)::numeric)));");
    }

    [Fact]
    public async Task BuildStatements_Tables_EmitsEachPartitionOfItsParentAndNotItsColumns()
    {
        await using var writer = CreateWriter();

        // The child is listed first on purpose: PARTITION OF needs the parent to exist, so the stage
        // reorders by the parent link instead of trusting the catalog's order.
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders_2026",
                SchemaRef = SalesSchemaRef,
                ParentRef = OrdersRef,
                PartitionBound = "FOR VALUES FROM (1) TO (100)",
                Columns = [Column("id", "integer")],
            },
            new TableDefinition
            {
                Ref = OrdersRef,
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                TableKind = ETableKind.Partitioned,
                PartitionStrategy = "RANGE (id)",
                Columns = [Column("id", "integer")],
            },
        };

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, objects)
            .Should()
            .Equal(
                "CREATE TABLE sales.orders (id integer) PARTITION BY RANGE (id);",
                "CREATE TABLE sales.orders_2026 PARTITION OF sales.orders "
                + "FOR VALUES FROM (1) TO (100);");
    }

    [Fact]
    public async Task BuildStatements_Tables_KeepsThePartitionClauseOfAPartitionThatIsAParent()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Ref = OrdersRef,
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                TableKind = ETableKind.Partitioned,
                PartitionStrategy = "RANGE (id)",
                Columns = [Column("id", "integer")],
            },
            new TableDefinition
            {
                Name = "orders_2026",
                SchemaRef = SalesSchemaRef,
                ParentRef = OrdersRef,
                TableKind = ETableKind.Partitioned,
                PartitionStrategy = "HASH (id)",
                PartitionBound = "FOR VALUES FROM (1) TO (100)",
                Columns = [Column("id", "integer")],
            },
        };

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, objects)
            .Should()
            .Equal(
                "CREATE TABLE sales.orders (id integer) PARTITION BY RANGE (id);",
                "CREATE TABLE sales.orders_2026 PARTITION OF sales.orders "
                + "FOR VALUES FROM (1) TO (100) PARTITION BY HASH (id);");
    }

    [Fact]
    public async Task BuildStatements_TablePointingAtAParentTheCatalogDoesNotName_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders_2026",
                SchemaRef = SalesSchemaRef,
                ParentRef = OutsideRef,
                PartitionBound = "FOR VALUES FROM (1) TO (100)",
                Columns = [Column("id", "integer")],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("partition parent");
    }

    [Fact]
    public async Task BuildStatements_TableWhoseParentIsNotInTheStage_IsRefused()
    {
        await using var writer = CreateWriter();

        // The map names sales.orders, so its name is known — but the caller handed the stage only the
        // child, and no order of these statements can create a partition before its parent.
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders_2026",
                SchemaRef = SalesSchemaRef,
                ParentRef = OrdersRef,
                PartitionBound = "FOR VALUES FROM (1) TO (100)",
                Columns = [Column("id", "integer")],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("parent is not among the tables");
    }

    [Fact]
    public async Task BuildStatements_InheritsChild_IsRefusedBecauseItsOwnColumnsAreUnknown()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Ref = OrdersRef,
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                Columns = [Column("id", "integer")],
            },
            new TableDefinition
            {
                Name = "orders_archive",
                SchemaRef = SalesSchemaRef,
                ParentRef = OrdersRef,
                Columns = [Column("id", "integer"), Column("archived_at", "date")],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, objects);

        // A child with a parent and no bound inherits rather than partitions, and PostgreSQL builds
        // one from the columns the child declares for itself — which the catalog does not record.
        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("orders_archive").And.Contain("inherited");
    }

    [Theory]
    [InlineData(ETableKind.Foreign)]
    [InlineData(ETableKind.Temporary)]
    public async Task BuildStatements_TableKindTheCatalogRecordsNoDetailsFor_IsRefused(
        ETableKind kind)
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                TableKind = kind,
                Columns = [Column("id", "integer")],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(kind.ToString());
    }

    [Fact]
    public async Task BuildStatements_TableCarryingExclusionConstraints_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "slots",
                SchemaRef = SalesSchemaRef,
                Columns = [Column("during", "tsrange")],
                ExclusionConstraints =
                [
                    new ExclusionConstraintDefinition { Name = "slots_no_overlap" },
                ],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, objects);

        // The format keeps the constraint's name and nothing of its operator or predicate, so the
        // table would come back accepting rows the source refused.
        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("slots_no_overlap");
    }

    [Fact]
    public async Task BuildStatements_Constraints_AddsEachForeignKeyOfEveryTable()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                ForeignKeys =
                [
                    new ForeignKeyDefinition
                    {
                        Name = "orders_customer_id_fkey",
                        Columns = ["customer_id"],
                        ReferencedTableRef = CustomersRef,
                        ReferencedColumns = ["id"],
                        OnUpdate = "NO ACTION",
                        OnDelete = "CASCADE",
                        IsDeferrable = true,
                        IsInitiallyDeferred = true,
                    },
                    new ForeignKeyDefinition
                    {
                        Name = "orders_parent_id_fkey",
                        Columns = ["parent_id"],
                        ReferencedTableRef = OrdersRef,
                        ReferencedColumns = ["id"],
                    },
                ],
            },
        };

        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Constraints, objects)
            .Should()
            .Equal(
                "ALTER TABLE sales.orders ADD CONSTRAINT orders_customer_id_fkey "
                + "FOREIGN KEY (customer_id) REFERENCES sales.customers (id) "
                + "ON UPDATE NO ACTION ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED;",
                "ALTER TABLE sales.orders ADD CONSTRAINT orders_parent_id_fkey "
                + "FOREIGN KEY (parent_id) REFERENCES sales.orders (id);");
    }

    [Fact]
    public async Task BuildStatements_ForeignKeyPointingAtAnUnnamedTable_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                ForeignKeys =
                [
                    new ForeignKeyDefinition
                    {
                        Name = "orders_customer_id_fkey",
                        Columns = ["customer_id"],
                        ReferencedTableRef = ObjectRef.Table("", "outside"),
                        ReferencedColumns = ["id"],
                    },
                ],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Constraints, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("orders_customer_id_fkey");
    }

    [Fact]
    public async Task BuildStatements_Indexes_RunsTheCapturedTextOfEachIndexTheSourceMade()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                Indexes =
                [
                    Index(
                        "orders_created_at_idx",
                        "CREATE INDEX orders_created_at_idx ON sales.orders USING btree "
                        + "(created_at DESC NULLS FIRST) WHERE status IS NOT NULL"),
                    Index(
                        "orders_total_idx",
                        "CREATE INDEX orders_total_idx ON sales.orders USING btree (total);"),
                ],
            },
        };

        // Sort order, operator class and the predicate survive only in this text, which is why the
        // stage runs it instead of rebuilding the statement from what the format structures.
        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Indexes, objects)
            .Should()
            .Equal(
                "CREATE INDEX orders_created_at_idx ON sales.orders USING btree "
                + "(created_at DESC NULLS FIRST) WHERE status IS NOT NULL;",
                "CREATE INDEX orders_total_idx ON sales.orders USING btree (total);");
    }

    [Fact]
    public async Task BuildStatements_Indexes_LeavesTheIndexesAConstraintAlreadyCreated()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                PrimaryKey = new PrimaryKeyDefinition { Name = "orders_pkey", Columns = ["id"] },
                UniqueConstraints =
                [
                    new UniqueConstraintDefinition
                    {
                        Name = "orders_customer_id_key",
                        Columns = ["customer_id"],
                    },
                ],
                Indexes =
                [
                    Index("orders_pkey", "CREATE UNIQUE INDEX orders_pkey ON sales.orders (id)"),
                    Index(
                        "orders_customer_id_key",
                        "CREATE UNIQUE INDEX orders_customer_id_key ON sales.orders (customer_id)"),
                    Index(
                        "orders_created_at_idx",
                        "CREATE INDEX orders_created_at_idx ON sales.orders (created_at)"),
                ],
            },
        };

        // The table statement creates both constraints inline, and PostgreSQL names the enforcing index
        // after the constraint, so the two captured indexes are already there: creating them again
        // fails with "relation already exists" and rolls the whole stage back.
        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Indexes, objects)
            .Should()
            .Equal("CREATE INDEX orders_created_at_idx ON sales.orders (created_at);");
    }

    [Fact]
    public async Task BuildStatements_Indexes_GivenAnotherKindOfObject_IsRefused()
    {
        await using var writer = CreateWriter();

        var act = () => PostgreSqlWriter.BuildStatements(
            "postgresql",
            SchemaStage.Indexes,
            [new IndexDefinition { Name = "orders_created_at_idx", TableRef = OrdersRef }]);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("TableDefinition");
    }

    [Fact]
    public async Task BuildStatements_IndexWithoutCapturedDefinition_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                Indexes = [new IndexDefinition { Name = "orders_created_at_idx", TableRef = OrdersRef }],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Indexes, objects);

        // The format gives an index a name, a table and whether it is unique: no columns to write a
        // statement from, so an index without its captured text cannot be restored.
        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("orders_created_at_idx");
    }

    [Fact]
    public async Task BuildStatements_IndexRecordedAsAFragment_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                Indexes =
                [
                    new IndexDefinition
                    {
                        Name = "orders_created_at_idx",
                        TableRef = OrdersRef,
                        NativeDefinition = new NativeDefinition(
                            "postgresql",
                            "CREATE INDEX orders_created_at_idx ON sales.orders",
                            Complete: false),
                    },
                ],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Indexes, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("fragment");
    }

    [Fact]
    public async Task BuildStatements_IndexTextFromAnotherDialect_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TableDefinition
            {
                Name = "orders",
                SchemaRef = SalesSchemaRef,
                Indexes =
                [
                    new IndexDefinition
                    {
                        Name = "orders_created_at_idx",
                        TableRef = OrdersRef,
                        NativeDefinition = new NativeDefinition(
                            "mysql",
                            "CREATE INDEX orders_created_at_idx ON orders (created_at)"),
                    },
                ],
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Indexes, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("mysql").And.Contain("postgresql");
    }

    [Fact]
    public async Task BuildStatements_Triggers_RunsTheCapturedTextOfEachTriggerTheSourceMade()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            Trigger(
                "tr_audit",
                "CREATE TRIGGER tr_audit BEFORE UPDATE OF total OR INSERT ON sales.orders "
                + "FOR EACH ROW WHEN (new.status IS NOT NULL) EXECUTE FUNCTION sales.audit()"),
            Trigger(
                "tr_notify",
                "CREATE TRIGGER tr_notify AFTER DELETE ON sales.orders FOR EACH STATEMENT "
                + "EXECUTE FUNCTION public.notify();"),
        };

        // The columns an UPDATE OF event names, the order its events were declared in, the WHEN clause
        // and the function's arguments exist in no field of the record, so the stage runs the text the
        // engine reported instead of writing a statement from what the format structures.
        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Triggers, objects)
            .Should()
            .Equal(
                "CREATE TRIGGER tr_audit BEFORE UPDATE OF total OR INSERT ON sales.orders "
                + "FOR EACH ROW WHEN (new.status IS NOT NULL) EXECUTE FUNCTION sales.audit();",
                "CREATE TRIGGER tr_notify AFTER DELETE ON sales.orders FOR EACH STATEMENT "
                + "EXECUTE FUNCTION public.notify();");
    }

    [Fact]
    public async Task BuildStatements_Triggers_DisablesATriggerTheSourceHadDisabled()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            Trigger(
                "tr_audit",
                "CREATE TRIGGER tr_audit AFTER INSERT ON sales.orders FOR EACH ROW "
                + "EXECUTE FUNCTION sales.audit()",
                enabledState: "D"),
        };

        // pg_get_triggerdef describes the trigger, never its enabled flag, and the statement it yields
        // always creates one that fires. The recorded state therefore needs the extra statement.
        PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Triggers, objects)
            .Should()
            .Equal(
                "CREATE TRIGGER tr_audit AFTER INSERT ON sales.orders FOR EACH ROW "
                + "EXECUTE FUNCTION sales.audit();",
                "ALTER TABLE sales.orders DISABLE TRIGGER tr_audit;");
    }

    [Fact]
    public async Task BuildStatements_Triggers_GivenAnotherKindOfObject_IsRefused()
    {
        await using var writer = CreateWriter();

        var act = () => PostgreSqlWriter.BuildStatements(
            "postgresql",
            SchemaStage.Triggers,
            [new TableDefinition { Name = "orders", SchemaRef = SalesSchemaRef }]);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("TriggerDefinition");
    }

    [Fact]
    public async Task BuildStatements_TriggerWithoutCapturedDefinition_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TriggerDefinition { Name = "tr_audit", TableRef = OrdersRef },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Triggers, objects);

        // The record names no function, so a trigger that arrived without its text cannot be written.
        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("tr_audit");
    }

    [Fact]
    public async Task BuildStatements_TriggerRecordedAsAFragment_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TriggerDefinition
            {
                Name = "tr_audit",
                TableRef = OrdersRef,
                NativeDefinition = new NativeDefinition(
                    "postgresql",
                    "CREATE TRIGGER tr_audit BEFORE UPDATE ON sales.orders",
                    Complete: false),
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Triggers, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("fragment");
    }

    [Fact]
    public async Task BuildStatements_TriggerTextFromAnotherDialect_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TriggerDefinition
            {
                Name = "tr_audit",
                TableRef = OrdersRef,
                NativeDefinition = new NativeDefinition(
                    "mysql",
                    "CREATE TRIGGER tr_audit AFTER INSERT ON orders FOR EACH ROW EXECUTE FUNCTION audit()"),
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Triggers, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("mysql").And.Contain("postgresql");
    }

    [Fact]
    public async Task BuildStatements_TriggerWithAStateThatMeansNeitherEnabledNorDisabled_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            Trigger(
                "tr_audit",
                "CREATE TRIGGER tr_audit AFTER INSERT ON sales.orders FOR EACH ROW "
                + "EXECUTE FUNCTION sales.audit()",
                enabledState: "R"),
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Triggers, objects);

        // Creating the trigger would leave it firing when the source kept it from firing in some of the
        // states this flag can hold, and the format gives the restore no statement for those.
        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("enabled state");
    }

    [Fact]
    public async Task BuildStatements_TriggerOnATableTheCatalogDoesNotName_IsRefused()
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[]
        {
            new TriggerDefinition
            {
                Name = "tr_audit",
                TableRef = ObjectRef.Table("", "outside"),
                NativeDefinition = new NativeDefinition(
                    "postgresql",
                    "CREATE TRIGGER tr_audit AFTER INSERT ON sales.outside FOR EACH ROW "
                    + "EXECUTE FUNCTION sales.audit()"),
            },
        };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Triggers, objects);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("table");
    }

    [Theory]
    [InlineData(SchemaStage.PostData)]
    [InlineData(SchemaStage.Publications)]
    [InlineData(SchemaStage.Subscriptions)]
    public async Task BuildStatements_AStageThisAdapterCannotServe_Refuses(SchemaStage stage)
    {
        await using var writer = CreateWriter();
        var objects = new ISchemaObject[] { new SchemaDefinition { Name = "sales" } };

        var act = () => PostgreSqlWriter.BuildStatements("postgresql", stage, objects);

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
                    CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public void BuildRowTargets_PlainColumns_AnswerEveryOneOfThemInCatalogOrder()
    {
        var targets = PostgreSqlWriter.BuildRowTargets(
        [
            StoredColumn("id", "integer", ordinal: 1),
            StoredColumn("total", "numeric(10,2)", ordinal: 2),
        ]);

        targets.Should().Equal(
            new PostgreSqlWriter.RowTarget("id", "integer", 0),
            new PostgreSqlWriter.RowTarget("total", "numeric(10,2)", 1));
    }

    [Fact]
    public void BuildRowTargets_ColumnListOutOfOrder_FollowsTheOrdinalBecauseThatIsTheExportOrder()
    {
        // A chunk stores a row as positional text, and the export selected the columns by ordinal, so
        // the slot a value answers is the ordinal's position — not the place the section happened to
        // list the column in.
        var targets = PostgreSqlWriter.BuildRowTargets(
        [
            StoredColumn("note", "text", ordinal: 3),
            StoredColumn("id", "integer", ordinal: 1),
            StoredColumn("total", "numeric(10,2)", ordinal: 2),
        ]);

        targets.Select(t => t.Column).Should().Equal("id", "total", "note");
        targets.Select(t => t.RowIndex).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void BuildRowTargets_GeneratedColumn_LeavesTheStatementAndKeepsItsSlot()
    {
        // PostgreSQL refuses an INSERT that names a generated column, so the token the catalog holds
        // for it is dropped — but the columns behind it still answer the same positions they did in
        // the stored row.
        var targets = PostgreSqlWriter.BuildRowTargets(
        [
            StoredColumn("id", "integer", ordinal: 1),
            StoredGeneratedColumn("display", "text", ordinal: 2),
            StoredColumn("note", "text", ordinal: 3),
        ]);

        targets.Should().Equal(
            new PostgreSqlWriter.RowTarget("id", "integer", 0),
            new PostgreSqlWriter.RowTarget("note", "text", 2));
    }

    [Fact]
    public void BuildRowTargets_TypeRecordedAsEmpty_RefusesInsteadOfCastingToNothing()
    {
        var act = () => PostgreSqlWriter.BuildRowTargets([StoredColumn("payload", "", ordinal: 1)]);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("payload");
    }

    [Fact]
    public void AppendRowValues_PlainText_WritesEachTokenAsALiteralOfItsOwnType()
    {
        var targets = PostgreSqlWriter.BuildRowTargets(
        [
            StoredColumn("note", "text", ordinal: 1),
            StoredColumn("total", "numeric(10,2)", ordinal: 2),
            StoredColumn("count", "integer", ordinal: 3),
        ]);
        var row = new[]
        {
            DataValue.FromText(ELogicalType.String, "O'Reilly"),
            DataValue.Null(ELogicalType.Decimal),
            DataValue.FromText(ELogicalType.Integer32, "42"),
        };

        var sb = new System.Text.StringBuilder();
        PostgreSqlWriter.AppendRowValues(sb, targets, row);
        sb.ToString().Should().Be("'O''Reilly'::text, NULL, '42'::integer");
    }

    [Fact]
    public void AppendRowValues_SkipsTheGeneratedColumnItCannotWrite()
    {
        var targets = PostgreSqlWriter.BuildRowTargets(
        [
            StoredColumn("id", "integer", ordinal: 1),
            StoredGeneratedColumn("display", "text", ordinal: 2),
        ]);
        var row = new[]
        {
            DataValue.FromText(ELogicalType.Integer32, "7"),
            DataValue.FromText(ELogicalType.String, "seven"),
        };

        var sb = new System.Text.StringBuilder();
        PostgreSqlWriter.AppendRowValues(sb, targets, row);
        sb.ToString().Should().Be("'7'::integer");
    }

    [Fact]
    public void AppendRowValues_RowShorterThanTheRecordedColumns_StopsRatherThanGuessingThePlace()
    {
        var targets = PostgreSqlWriter.BuildRowTargets(
        [
            StoredColumn("id", "integer", ordinal: 1),
            StoredColumn("note", "text", ordinal: 2),
        ]);
        var row = new[] { DataValue.FromText(ELogicalType.Integer32, "7") };

        var sb = new System.Text.StringBuilder();
        var act = () => PostgreSqlWriter.AppendRowValues(sb, targets, row);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("note").And.Contain("1 value");
    }

    [Fact]
    public void BuildInsertStatement_AssemblesOneValuesListPerStoredRow() =>
        PostgreSqlWriter.BuildInsertStatement(
                "\"sales\".\"orders\"",
                "\"id\", \"note\"",
                [
                    new PostgreSqlWriter.RowTarget("id", "integer", 0),
                    new PostgreSqlWriter.RowTarget("note", "text", 1),
                ],
                [
                    new[]
                    {
                        DataValue.FromText(ELogicalType.Integer32, "7"),
                        DataValue.FromText(ELogicalType.String, "a b"),
                    },
                    new[]
                    {
                        DataValue.FromText(ELogicalType.Integer32, "8"),
                        DataValue.Null(ELogicalType.String),
                    },
                ])
            .Should()
            .Be(
                "INSERT INTO \"sales\".\"orders\" (\"id\", \"note\") VALUES "
                + "('7'::integer, 'a b'::text), ('8'::integer, NULL);");

    [Fact]
    public void AppendRowValues_ValueStoredAsAPayload_GoesOnTheWireAsAByteaStreamParameter()
    {
        // Design §5: a value the catalog keeps outside the row file is bound as a parameter over its
        // payload, so its bytes never enter the statement text as hex and never become one string.
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03 };
        var targets = PostgreSqlWriter.BuildRowTargets([StoredColumn("payload", "bytea", ordinal: 1)]);
        var row = new[] { DataValue.FromPayload(ELogicalType.Binary, new StoredPayload(bytes)) };
        var parameters = new List<NpgsqlParameter>();

        var sb = new StringBuilder();
        PostgreSqlWriter.AppendRowValues(sb, targets, row, parameters);

        sb.ToString().Should().Be("@blob0");
        parameters.Should().ContainSingle();
        parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.Bytea);
        parameters[0].Value.Should().BeAssignableTo<Stream>();

        using var stream = (Stream)parameters[0].Value!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public void AppendRowValues_PayloadWithoutAParameterList_RefusesRatherThanLoseTheBytes()
    {
        // The bytes exist only in the payload; rendering the placeholder into a statement nobody will
        // bind is how a value would silently arrive as nothing, so the call that cannot bind refuses.
        var targets = PostgreSqlWriter.BuildRowTargets([StoredColumn("payload", "bytea", ordinal: 1)]);
        var row = new[]
        {
            DataValue.FromPayload(ELogicalType.Binary, new StoredPayload([0x01, 0x02])),
        };

        var sb = new StringBuilder();
        var act = () => PostgreSqlWriter.AppendRowValues(sb, targets, row);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("payload");
    }

    [Fact]
    public void AppendRowValues_BinaryValueStoredInline_IsStillALiteralCast()
    {
        // The threshold decision belongs to the catalog; a value that stayed in the row file arrives as
        // the text it was stored as, and the plugin has no reason to change how it is bound.
        var targets = PostgreSqlWriter.BuildRowTargets([StoredColumn("payload", "bytea", ordinal: 1)]);
        var row = new[] { DataValue.FromText(ELogicalType.Binary, "\\x4142") };

        var sb = new StringBuilder();
        PostgreSqlWriter.AppendRowValues(sb, targets, row);

        sb.ToString().Should().Be("'\\x4142'::bytea");
    }

    [Fact]
    public void BuildInsertStatement_PayloadsAcrossRows_NumberTheirParametersInStatementOrder()
    {
        var targets = PostgreSqlWriter.BuildRowTargets([StoredColumn("payload", "bytea", ordinal: 1)]);
        var batch = new[]
        {
            new[] { DataValue.FromPayload(ELogicalType.Binary, new StoredPayload([0x01, 0x02])) },
            new[] { DataValue.FromPayload(ELogicalType.Binary, new StoredPayload([0x03, 0x04])) },
        };
        var parameters = new List<NpgsqlParameter>();

        var sql = PostgreSqlWriter.BuildInsertStatement(
            "\"public\".\"images\"", "\"payload\"", targets, batch, parameters);

        sql.Should().Be("INSERT INTO \"public\".\"images\" (\"payload\") VALUES (@blob0), (@blob1);");
        parameters.Select(p => p.ParameterName).Should().Equal("blob0", "blob1");
    }

    [Fact]
    public void BuildInsertStatement_AValueOfAMegabyte_CostsTheStatementTextNothingButItsPlaceholder()
    {
        // The batch budget R7 measures is statement text. A payload's bytes travel as parameter data, so
        // a megabyte-sized value cannot inflate the measured statement the way its hex rendering would
        // have — which is what keeps a large-value batch from being split to pieces by the budget.
        var payload = new byte[1024 * 1024];
        var targets = PostgreSqlWriter.BuildRowTargets([StoredColumn("payload", "bytea", ordinal: 1)]);
        var batch = new[]
        {
            new[] { DataValue.FromPayload(ELogicalType.Binary, new StoredPayload(payload)) },
        };
        var parameters = new List<NpgsqlParameter>();

        var sql = PostgreSqlWriter.BuildInsertStatement(
            "\"public\".\"images\"", "\"payload\"", targets, batch, parameters);

        sql.Should().HaveLength("INSERT INTO \"public\".\"images\" (\"payload\") VALUES (@blob0);".Length);
        sql.Should().NotContain("\\x", "the bytes never become hex inside the statement text");
    }

    /// <summary>
    /// A binary value held outside the row file, as the interchange reader hands one over.
    /// </summary>
    private sealed class StoredPayload(byte[] bytes) : IBinaryPayload
    {
        public long Length => bytes.LongLength;

        public Stream OpenRead() => new MemoryStream(bytes, writable: false);
    }

    [Fact]
    public async Task WriteTableRowsAsync_CatalogRecordingNoWritableColumn_Refuses()
    {
        await using var writer = CreateWriter();

        var act = () => writer.WriteTableRowsAsync(
            new TableRef("sales", "orders"),
            [StoredGeneratedColumn("display", "text", ordinal: 1)],
            ReadRowBatch(),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<NotSupportedException>(act);

        error.Message.Should().Contain("sales.orders");
        _rowBatchPulled.Should().BeFalse(
            "a table nothing can be written into has no reason to have its rows read");
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
    public void CountTargetObjects_ModelHoldingRoutines_SeparatesFunctionsFromProcedures()
    {
        // The live model keeps both in one collection while the interchange model names them apart, so
        // a restore that created one of each has to be checked against one of each.
        var counted = PostgreSqlWriter.CountTargetObjects(
            InterchangeTestModel.Empty() with
            {
                Functions =
                [
                    new Models.FunctionDefinition(
                        "public", "fn_notify", "plpgsql", "trigger", "CREATE FUNCTION ...", [],
                        CanonicalModel.Enums.EFunctionVolatility.Volatile, false, false, null, false),
                    new Models.FunctionDefinition(
                        "public", "add_customer", "plpgsql", "integer", "CREATE FUNCTION ...", [],
                        CanonicalModel.Enums.EFunctionVolatility.Volatile, false, false, null, false),
                    new Models.FunctionDefinition(
                        "public", "run_cleanup", "plpgsql", "void", "CREATE PROCEDURE ...", [],
                        CanonicalModel.Enums.EFunctionVolatility.Volatile, false, false, null, true),
                ],
            });

        counted[EObjectKind.Function].Should().Be(2);
        counted[EObjectKind.Procedure].Should().Be(1);
    }

    [Fact]
    public void CountTargetObjects_SystemSchemas_AreCountedOnlyWhenTheCatalogNamesThem()
    {
        var target = InterchangeTestModel.Empty() with
        {
            Schemas =
            [
                new Models.SchemaDefinition("public", "test_user"),
                new Models.SchemaDefinition("pg_catalog", "test_user", IsSystem: true),
            ],
        };

        // A catalog that names the system schema is answered by the copy the target already holds.
        PostgreSqlWriter.CountTargetObjects(target, ["public", "pg_catalog"])[EObjectKind.Schema]
            .Should().Be(2);

        // A catalog that names only its own schemas is answered by the target's user schemas alone.
        PostgreSqlWriter.CountTargetObjects(target, ["public"])[EObjectKind.Schema].Should().Be(1);

        // A system schema the target lacks is not counted at all: the restore cannot create it.
        PostgreSqlWriter.CountTargetObjects(
                InterchangeTestModel.Empty() with
                {
                    Schemas = [new Models.SchemaDefinition("public", "test_user")],
                },
                ["public", "pg_catalog"])[EObjectKind.Schema]
            .Should().Be(1);
    }

    [Fact]
    public void CountTargetObjects_KindTheLiveModelHasNoCollectionFor_IsLeftOut()
    {
        var counted = PostgreSqlWriter.CountTargetObjects(InterchangeTestModel.Empty());

        counted.ContainsKey(EObjectKind.Grant).Should().BeFalse(
            "a zero would be reported as a target holding nothing, where the truth is that this "
            + "writer cannot count the kind at all");
    }

    [Fact]
    public async Task ValidateAsync_TargetHoldingWhatTheCatalogRecords_PassesWithItsCounts()
    {
        Given(InterchangeTestModel.Empty() with { Tables = [Table("orders"), Table("customers")] });
        await using var writer = CreateWriter();

        var report = await writer.ValidateAsync(
            new TargetExpectation
            {
                ObjectCounts = new Dictionary<EObjectKind, int> { [EObjectKind.Table] = 2 },
            },
            CancellationToken.None);

        report.Passed.Should().BeTrue();
        report.Issues.Should().BeEmpty();
        report.FoundRowCounts.Should().BeEmpty();
        report.FoundObjectCounts[EObjectKind.Table].Should().Be(2);
    }

    [Fact]
    public async Task ValidateAsync_TargetShortOfTheCatalog_ReportsTheMismatchAsAnError()
    {
        Given(InterchangeTestModel.Empty() with { Tables = [Table("orders")] });
        await using var writer = CreateWriter();

        var report = await writer.ValidateAsync(
            new TargetExpectation
            {
                ObjectCounts = new Dictionary<EObjectKind, int> { [EObjectKind.Table] = 3 },
            },
            CancellationToken.None);

        report.Passed.Should().BeFalse();
        report.Issues.Should().ContainSingle()
            .Which.Message.Should().Contain("3").And.Contain("1").And.Contain("Table");
    }

    [Fact]
    public async Task ValidateAsync_CatalogNamingASystemSchema_PassesAgainstTheTargetsOwnCopy()
    {
        // A restore creates no system schema, so the target's own copy is what the count is
        // answered by — which is what keeps a catalog captured before this rule restorable.
        Given(
            InterchangeTestModel.Empty() with
            {
                Schemas =
                [
                    new Models.SchemaDefinition("public", "test_user"),
                    new Models.SchemaDefinition("pg_catalog", "test_user", IsSystem: true),
                ],
            });
        await using var writer = CreateWriter();

        var report = await writer.ValidateAsync(
            new TargetExpectation
            {
                ObjectCounts = new Dictionary<EObjectKind, int> { [EObjectKind.Schema] = 2 },
                SchemaNames = ["public", "pg_catalog"],
            },
            CancellationToken.None);

        report.Passed.Should().BeTrue();
        report.FoundObjectCounts[EObjectKind.Schema].Should().Be(2);
    }

    [Fact]
    public async Task ValidateAsync_KindThisWriterCannotCount_IsReportedAsUnchecked()
    {
        Given(InterchangeTestModel.Empty());
        await using var writer = CreateWriter();

        var report = await writer.ValidateAsync(
            new TargetExpectation
            {
                ObjectCounts = new Dictionary<EObjectKind, int> { [EObjectKind.Grant] = 4 },
            },
            CancellationToken.None);

        report.Passed.Should().BeTrue("nothing was found to be wrong, but neither was anything checked");
        report.Issues.Should().ContainSingle()
            .Which.Severity.Should().Be(EIssueSeverity.Warning);
        report.FoundObjectCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ExpectationNamingNothing_AskedForNoRowCountAndTookNoConnection()
    {
        Given(InterchangeTestModel.Empty());
        await using var writer = CreateWriter();

        var report = await writer.ValidateAsync(new TargetExpectation(), CancellationToken.None);

        report.Passed.Should().BeTrue();
        report.Issues.Should().BeEmpty();
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
                    CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
        await writer.Invoking(w => w.ValidateAsync(new TargetExpectation(), CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// The schema id the map below answers. A staged object names its parent by id alone, so a test
    /// that wants an object placed must name the id the same way the catalog reader did.
    /// </summary>
    private static readonly ObjectRef SalesSchemaRef = ObjectRef.Schema("sales");
    private static readonly ObjectRef OrdersRef = ObjectRef.Table("sales", "orders");
    private static readonly ObjectRef CustomersRef = ObjectRef.Table("sales", "customers");
    private static readonly ObjectRef OutsideRef = ObjectRef.Table("sales", "outside");

    private static ColumnDefinition Column(
        string name,
        string type,
        bool nullable = true) =>
        new()
        {
            Name = name,
            NativeType = new NativeType(type),
            Nullable = nullable,
        };

    /// <summary>
    /// A column as the catalog records one for a table whose rows are being written: its position in
    /// the stored row is the ordinal the export selected by.
    /// </summary>
    private static ColumnDefinition StoredColumn(string name, string type, int ordinal) =>
        new()
        {
            Name = name,
            NativeType = new NativeType(type),
            Ordinal = ordinal,
            Nullable = true,
        };

    /// <summary>A column the server computes for itself, which no INSERT may name.</summary>
    private static ColumnDefinition StoredGeneratedColumn(string name, string type, int ordinal) =>
        StoredColumn(name, type, ordinal) with
        {
            Generated = new DatabaseExpression("postgresql", $"concat('{name}')"),
        };

    /// <summary>
    /// A type as the catalog's native file records one: which kind of type it is, where it lives, and the
    /// complete statement the capture assembled for it.
    /// </summary>
    private static NativeObjectDefinition Type(
        EObjectKind kind,
        string schemaName,
        string name,
        string statement) =>
        new()
        {
            Kind = kind,
            SchemaName = schemaName,
            Name = name,
            NativeDefinition = new NativeDefinition("postgresql", statement),
        };

    /// <summary>
    /// An index as the catalog records one: its name, the table it belongs to, and the complete
    /// statement the engine reported for it.
    /// </summary>
    private static IndexDefinition Index(string name, string definition) =>
        new()
        {
            Name = name,
            TableRef = OrdersRef,
            NativeDefinition = new NativeDefinition("postgresql", definition),
        };

    /// <summary>
    /// A trigger as the catalog records one: its name, the table it fires on, and the complete statement
    /// the engine reported for it.
    /// </summary>
    private static TriggerDefinition Trigger(
        string name,
        string definition,
        string enabledState = "O") =>
        new()
        {
            Name = name,
            TableRef = OrdersRef,
            EnabledState = enabledState,
            NativeDefinition = new NativeDefinition("postgresql", definition),
        };

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
