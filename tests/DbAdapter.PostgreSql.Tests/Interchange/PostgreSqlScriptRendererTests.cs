using CanonicalModel.Interchange;

using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;
using DbAdapter.PostgreSql.Interchange;

using FluentAssertions;

using Xunit;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The adapter's <see cref="ISqlScriptRenderer"/>: the text a store writes into the catalog's script files,
/// which is the restore's own text made re-runnable, plus the one insert shape the restore never emits.
/// </summary>
/// <remarks>
/// No connection is opened and no file is written: the role answers text from the catalog model, so every
/// assertion here is about the words in that text — what it says, what it must not say, and what it refuses
/// to say. How each statement class is guarded is <see cref="ScriptStatementGuardTests"/>' subject; these
/// tests check that the renderer hands that guard the writer's text, stage by stage.
/// </remarks>
public class PostgreSqlScriptRendererTests
{
    private const string DialectName = "postgresql";

    /// <summary>The stages the product hands to the writer, in the order it hands them.</summary>
    private static readonly SchemaStage[] RestoreStages =
    [
        SchemaStage.Schemas,
        SchemaStage.Types,
        SchemaStage.Sequences,
        SchemaStage.Tables,
        SchemaStage.Routines,
        SchemaStage.Views,
        SchemaStage.Constraints,
        SchemaStage.Indexes,
        SchemaStage.Triggers,
    ];

    [Fact]
    public void Dialect_IsTheOneTheRendererWasGiven()
    {
        // The dialect is the adapter's own identity, passed in rather than assumed, so the product can hold
        // the script against the one adapter that can execute it (D11).
        Renderer().Dialect.Should().Be(DialectName);
        new PostgreSqlScriptRenderer("another-dialect").Dialect.Should().Be("another-dialect");
    }

    [Fact]
    public void Dialect_WhichIsMissing_IsRefused()
    {
        var act = () => new PostgreSqlScriptRenderer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void HeaderStatements_AreTheTwoSessionSettingsEveryLiteralIsReadUnder()
    {
        // Session-level, not LOCAL: a local setting outside a transaction block is discarded, so the header
        // states both for the whole session (D2).
        var header = Renderer().HeaderStatements();

        header.Should().Equal(
            "SET TIME ZONE 'UTC';",
            "SET standard_conforming_strings = 'on';");

        header.Should().NotContain(s => s.Contains("LOCAL", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(SchemaStage.Schemas, "CREATE SCHEMA IF NOT EXISTS sales;")]
    [InlineData(SchemaStage.Types, "CREATE TYPE public.mood AS ENUM ('sad', 'happy');")]
    [InlineData(SchemaStage.Sequences, "CREATE SEQUENCE IF NOT EXISTS public.order_seq AS bigint")]
    [InlineData(SchemaStage.Tables, "CREATE TABLE IF NOT EXISTS public.customers (")]
    [InlineData(SchemaStage.Routines, "CREATE OR REPLACE FUNCTION public.fn_notify()")]
    [InlineData(SchemaStage.Views, "CREATE OR REPLACE VIEW public.v_active AS")]
    [InlineData(SchemaStage.Constraints, "ALTER TABLE public.orders ADD CONSTRAINT fk_orders_customer")]
    [InlineData(SchemaStage.Indexes, "CREATE INDEX IF NOT EXISTS ix_customers_name")]
    [InlineData(SchemaStage.Triggers, "DROP TRIGGER IF EXISTS tr_audit ON public.customers;")]
    public void SchemaStatements_EveryStageTheRestoreApplies_WritesTheWritersTextGuarded(
        SchemaStage stage,
        string expected)
    {
        var statements = Renderer().SchemaStatements(stage, ObjectsFor(stage));

        statements.Should().NotBeEmpty();
        statements.Should().Contain(
            statement => statement.Contains(expected, StringComparison.Ordinal),
            "the stage's text is the writer's own, made re-runnable");
    }

    [Fact]
    public void SchemaStatements_MaterializedView_AddsTheKeywordTheGeneratorLeavesOut()
    {
        var statements = Renderer().SchemaStatements(
            SchemaStage.Views,
            [View("reporting", "mv_totals", materialized: true)]);

        statements.Should().ContainSingle();
        statements[0].Should().StartWith("CREATE MATERIALIZED VIEW IF NOT EXISTS reporting.mv_totals AS");
        statements[0].Should().EndWith("WITH NO DATA;");
    }

    [Fact]
    public void SchemaStatements_LineBreaksTheGeneratorWrote_AreWrittenAsOneLineBreak()
    {
        // The generator separates a view's body with the platform's own line break. A file that mixed the
        // two would not be byte-identical across platforms, which is what the script promises (FR11).
        var statements = RestoreStages
            .SelectMany(stage => Renderer().SchemaStatements(stage, ObjectsFor(stage)))
            .ToList();

        statements.Should().NotBeEmpty();
        statements.Should().NotContain(statement => statement.Contains('\r'));
    }

    [Fact]
    public void SchemaStatements_ForeignTable_IsRefused()
    {
        // A foreign table needs a server and its options, and the catalog keeps neither, so creating it as
        // a plain table would report a table the source never had.
        var table = Table("orders") with { TableKind = ETableKind.Foreign };

        var act = () => Renderer().SchemaStatements(SchemaStage.Tables, [table]);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Foreign");
    }

    [Fact]
    public void SchemaStatements_NothingCreatesAnExtension()
    {
        // An extension's statement is not a statement the product writes: the native file is the only
        // source of types, and no stage of the format takes an extension from it.
        NativeObjectRouting.StageFor(EObjectKind.Extension).Should().BeNull();

        var statements = RestoreStages
            .SelectMany(stage => Renderer().SchemaStatements(stage, ObjectsFor(stage)))
            .ToList();

        statements.Should().NotContain(
            statement => statement.Contains("EXTENSION", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BeginTableBlock_IsTheBannerAndTheTransactionTheRestoreOpens()
    {
        // The banner, BEGIN and both LOCAL settings are the block's, so the product can close and reopen them
        // wherever it cuts a file (D4).
        var statements = Renderer().BeginTableBlock(Orders(), 4321, ScriptBlockPart.Whole);

        statements.Should().Equal(
            "-- public.orders: 4321 row(s).",
            "BEGIN;",
            "SET LOCAL TIME ZONE 'UTC';",
            "SET LOCAL standard_conforming_strings = 'on';");
        statements.Should().NotContain(statement => statement.StartsWith("-- part", StringComparison.Ordinal));
    }

    [Fact]
    public void BeginTableBlock_OfAPart_SaysWhichPartItContinues()
    {
        // A table larger than one file is several transactions, so each says which part it is (D4, D7).
        var statements = Renderer().BeginTableBlock(Orders(), 120, new ScriptBlockPart(3));

        statements.Should().Equal(
            "-- public.orders: 120 row(s).",
            "-- part 3",
            "BEGIN;",
            "SET LOCAL TIME ZONE 'UTC';",
            "SET LOCAL standard_conforming_strings = 'on';");
    }

    [Fact]
    public void EndTableBlock_ClosesTheTransactionTheBlockOpened()
    {
        Renderer().EndTableBlock().Should().Equal("COMMIT;");
    }

    [Fact]
    public async Task InsertStatements_CarryNoTransactionAndNoBannerOfTheirOwn()
    {
        // Where a block is cut is the product's decision, so the inserts never open or close one: a file the
        // product cuts must not gain a second BEGIN, and a continuation must not repeat the banner (D4).
        var statements = await Render(Orders(), Row(1, "12.50", "first"));

        statements.Should().ContainSingle("one batch is one insert and nothing else");
        statements[0].Should().StartWith("INSERT INTO public.orders");
        statements[0].Should().EndWith("ON CONFLICT DO NOTHING;");
        statements.Should().NotContain(statement => statement == "BEGIN;" || statement == "COMMIT;");
        statements.Should().NotContain(statement => statement.Contains("SET LOCAL", StringComparison.Ordinal));
        statements.Should().NotContain(statement => statement.StartsWith("-- public.orders:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InsertStatements_PairsEveryStatementWithTheRowsItsBatchCarries()
    {
        // The count is what lets the product attribute a file's rows without a second pass over the data (D9).
        var batches = await CollectBatches(
            Renderer().InsertStatements(Orders(), ToAsync([Row(1, "1.00", "a"), Row(2, "2.00", "b")])));

        batches.Should().ContainSingle();
        batches[0].Rows.Should().Be(2);
        batches[0].Statement.Should().StartWith("INSERT INTO public.orders");
    }

    [Fact]
    public async Task InsertStatements_TextAndNull_AreTheTypedLiteralsTheRestoreBinds()
    {
        // A token is the engine's own output text, so it is handed back through the same type's input
        // function by casting the literal — which works for an enum or a domain, where a parameter cannot.
        var statements = await Render(
            Orders(),
            Row(1, "12.50", "it's"),
            [
                DataValue.FromText(ELogicalType.Integer64, "2"),
                DataValue.Null(ELogicalType.Decimal),
                DataValue.Null(ELogicalType.String),
            ]);

        statements.Should().Contain(
            "INSERT INTO public.orders (id, total, note) VALUES "
            + "('1'::bigint, '12.50'::numeric(10,2), 'it''s'::text), "
            + "('2'::bigint, NULL, NULL) ON CONFLICT DO NOTHING;",
            "one batch carries both rows, and the quote in the text is doubled");
    }

    [Fact]
    public async Task InsertStatements_StoredPayload_IsWrittenIntoTheStatementAsBase64()
    {
        // A file has no parameters, so the bytes a restore would bind are written into the statement
        // instead. Base64 cannot contain the quote that would end the literal, and is 4/3 of the payload
        // against hex's 2× (D5).
        var bytes = new byte[] { 0x00, 0x01, 0xFF };
        var expected = Convert.ToBase64String(bytes);

        var statements = await Render(
            Photos(),
            [
                DataValue.FromText(ELogicalType.Integer64, "1"),
                DataValue.FromPayload(ELogicalType.Binary, new StoredPayload(bytes)),
            ]);

        statements.Should().Contain(
            "INSERT INTO public.photos (id, payload) VALUES "
            + $"('1'::bigint, decode('{expected}', 'base64')::bytea) ON CONFLICT DO NOTHING;");

        statements.Should().NotContain(statement => statement.Contains("\\x", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InsertStatements_TableWithAConflictArbiter_DoesNotOverwriteARowItFinds()
    {
        // A target-less ON CONFLICT DO NOTHING covers every arbiter the table has, so the statement skips a
        // row that is already there without one being named — and never replaces what the target holds.
        var statements = await Render(Orders(), Row(1, "1.00", "a"));

        var insert = statements.Single(s => s.StartsWith("INSERT", StringComparison.Ordinal));

        insert.Should().EndWith("ON CONFLICT DO NOTHING;");
        insert.Should().NotContain("DO UPDATE");
    }

    [Fact]
    public async Task InsertStatements_UniqueKeyThatMayHoldANull_IsNoArbiter()
    {
        // Two nulls are never equal, so a second run inserts a second row carrying one and nothing
        // conflicts: the key proves nothing about the rows the target already holds, and the whole row is
        // compared instead (D4).
        var table = Table("tags") with
        {
            UniqueConstraints = [UniqueConstraint("uq_tags_note", "note")],
        };

        var insert = await SingleInsert(table, Row(1, "1.00", "a"));

        insert.Should().Contain("WHERE NOT EXISTS (");
        insert.Should().NotContain("ON CONFLICT");
    }

    [Fact]
    public async Task InsertStatements_UniqueKeyOfNotNullColumns_IsAnArbiter()
    {
        // Every row the catalog can hold reads a value in that key, so a second run of the same row does
        // conflict and is skipped by the shorter form.
        var table = Table("tags") with
        {
            Columns =
            [
                Column("id", "bigint", 1),
                Column("total", "numeric(10,2)", 2),
                Column("note", "text", 3) with { Nullable = false },
            ],
            UniqueConstraints = [UniqueConstraint("uq_tags_note", "note")],
        };

        var insert = await SingleInsert(table, Row(1, "1.00", "a"));

        insert.Should().EndWith("ON CONFLICT DO NOTHING;");
        insert.Should().NotContain("WHERE NOT EXISTS");
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX ix_tags_note ON public.tags USING btree (note) WHERE (note IS NOT NULL)")]
    [InlineData("CREATE UNIQUE INDEX ix_tags_note ON public.tags USING btree (lower(note))")]
    [InlineData("CREATE UNIQUE INDEX ix_tags_note ON public.tags USING btree (note DESC)")]
    public async Task InsertStatements_UniqueIndexThatDoesNotCoverEveryRow_IsNoArbiter(string definition)
    {
        // A partial index says nothing about the rows outside its predicate, and a list that is an
        // expression or a sort clause proves no plain column: in both cases a second run could insert a
        // row the target already holds, so the whole row is compared (D4).
        var table = Table("tags") with
        {
            Columns =
            [
                Column("id", "bigint", 1),
                Column("total", "numeric(10,2)", 2),
                Column("note", "text", 3) with { Nullable = false },
            ],
            Indexes =
            [
                new IndexDefinition
                {
                    Ref = ObjectRef.SubObject("index", "public", "tags", "ix_tags_note"),
                    Name = "ix_tags_note",
                    TableRef = ObjectRef.Table("public", "tags"),
                    IsUnique = true,
                    NativeDefinition = new NativeDefinition(DialectName, definition),
                },
            ],
        };

        var insert = await SingleInsert(table, Row(1, "1.00", "a"));

        insert.Should().Contain("WHERE NOT EXISTS (");
        insert.Should().NotContain("ON CONFLICT");
    }

    [Fact]
    public async Task InsertStatements_UnconditionalUniqueIndexOnNotNullColumns_IsAnArbiter()
    {
        // The index's own text says the list is plain columns and no predicate narrows them, which is what
        // makes it a key for every row (D4).
        var table = Table("tags") with
        {
            Columns =
            [
                Column("id", "bigint", 1),
                Column("total", "numeric(10,2)", 2),
                Column("note", "text", 3) with { Nullable = false },
            ],
            Indexes =
            [
                new IndexDefinition
                {
                    Ref = ObjectRef.SubObject("index", "public", "tags", "ix_tags_note"),
                    Name = "ix_tags_note",
                    TableRef = ObjectRef.Table("public", "tags"),
                    IsUnique = true,
                    NativeDefinition = new NativeDefinition(
                        DialectName,
                        "CREATE UNIQUE INDEX ix_tags_note ON public.tags USING btree (note)"),
                },
            ],
        };

        var insert = await SingleInsert(table, Row(1, "1.00", "a"));

        insert.Should().EndWith("ON CONFLICT DO NOTHING;");
    }

    [Fact]
    public async Task InsertStatements_KeylessTable_InsertsOnlyARowTheTargetDoesNotHold()
    {
        // The catalog records no arbiter for this table, so the whole row is its identity: the values are
        // written once into a VALUES list the comparison reads back by name, and IS NOT DISTINCT FROM is
        // what gives a null match a null.
        var table = Table("key_value") with
        {
            Columns = [Column("payload", "text", 1)],
        };

        var insert = await SingleInsert(table, [DataValue.FromText(ELogicalType.String, "a")]);

        insert.Should().StartWith("INSERT INTO public.key_value (payload)\nSELECT v.payload\nFROM (VALUES\n");
        insert.Should().Contain(") AS v(payload)");
        insert.Should().Contain("WHERE NOT EXISTS (");
        insert.Should().Contain("t.payload IS NOT DISTINCT FROM v.payload");
        insert.Should().NotContain("ON CONFLICT");
    }

    [Fact]
    public async Task InsertStatements_KeylessTable_TypesAnEmptyColumnSoTheListIsNotReadAsText()
    {
        // The VALUES list of the keyless form is typed before the target's columns are known, so an
        // untyped NULL is read as text and the insert fails against a column of another type — and a
        // column whose every value is null is exactly that case (D5).
        var table = Table("key_value") with
        {
            Columns = [Column("payload", "text", 1), Column("total", "numeric(10,2)", 2)],
        };

        var insert = await SingleInsert(
            table,
            [DataValue.Null(ELogicalType.String), DataValue.FromText(ELogicalType.Decimal, "1.00")]);

        insert.Should().Contain("(NULL::text, '1.00'::numeric(10,2))");
        insert.Should().NotContain(", NULL,");
    }

    [Fact]
    public async Task InsertStatements_GeneratedColumn_IsNotAmongTheColumns()
    {
        // PostgreSQL computes a generated column from the others, so naming it in an INSERT is an error.
        // Its value still occupies a position in the stored row, which is why the row below carries two.
        var table = Table("customers") with
        {
            Columns =
            [
                Column("id", "bigint", 1),
                Column("total", "integer", 2) with
                {
                    Generated = new DatabaseExpression(DialectName, "(id * 2)"),
                },
            ],
            PrimaryKey = PrimaryKey("pk_customers", "id"),
        };

        var statements = await Render(
            table,
            [
                DataValue.FromText(ELogicalType.Integer64, "7"),
                DataValue.FromText(ELogicalType.Integer32, "14"),
            ]);

        statements.Should().Contain(
            "INSERT INTO public.customers (id) VALUES ('7'::bigint) ON CONFLICT DO NOTHING;");
    }

    [Fact]
    public async Task InsertStatements_TableWithNoScriptableColumn_IsRefused()
    {
        // A statement with an empty column list fails on the server, and leaving the rows out would
        // report a file that omits them as complete.
        var table = Table("customers") with
        {
            Columns =
            [
                Column("total", "integer", 1) with
                {
                    Generated = new DatabaseExpression(DialectName, "(1)"),
                },
            ],
        };

        var act = async () => await Render(table, [DataValue.FromText(ELogicalType.Integer32, "1")]);

        (await act.Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("customers");
    }

    [Fact]
    public async Task InsertStatements_TableWhoseSchemaIsNotNamed_IsRefused()
    {
        var table = Table("orders") with { SchemaRef = ObjectRef.Schema(string.Empty) };

        var act = async () => await Render(table, Row(1, "1.00", "a"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("does not name the schema");
    }

    [Fact]
    public void TailStatements_ValidateEveryKeyAndRefreshEveryMaterializedView_ButNameNoSequence()
    {
        // VALIDATE on a key that is already validated is a no-op, so a second run costs nothing; the
        // refresh makes a materialized view's contents match the data the same run wrote. A sequence is
        // deliberately absent: the catalog records a declaration and never a current value (D6).
        var statements = Renderer().TailStatements(
            [Orders()],
            [View("reporting", "mv_totals", materialized: true), View("public", "v_active", materialized: false)]);

        statements.Should().Equal(
            "ALTER TABLE public.orders VALIDATE CONSTRAINT fk_orders_customer;",
            "REFRESH MATERIALIZED VIEW reporting.mv_totals;");

        statements.Should().NotContain(
            statement => statement.Contains("SEQUENCE", StringComparison.OrdinalIgnoreCase)
                         || statement.Contains("setval", StringComparison.OrdinalIgnoreCase));
    }

    private static PostgreSqlScriptRenderer Renderer() => new(DialectName);

    /// <summary>The statements one table's block is made of, read out of the stream the renderer answers.</summary>
    private static async Task<IReadOnlyList<string>> Render(TableDefinition table, params DataValue[][] rows) =>
        (await CollectBatches(Renderer().InsertStatements(table, ToAsync(rows))))
            .Select(batch => batch.Statement)
            .ToList();

    private static async Task<List<(string Statement, int Rows)>> CollectBatches(
        IAsyncEnumerable<(string Statement, int Rows)> statements)
    {
        var collected = new List<(string Statement, int Rows)>();

        await foreach (var statement in statements)
        {
            collected.Add(statement);
        }

        return collected;
    }

    /// <summary>The single insert statement a block writes, leaving out its banner and its transaction.</summary>
    private static async Task<string> SingleInsert(TableDefinition table, params DataValue[][] rows) =>
        (await Render(table, rows)).Single(s => s.StartsWith("INSERT", StringComparison.Ordinal));

    private static async IAsyncEnumerable<DataValue[]> ToAsync(IReadOnlyList<DataValue[]> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static TableDefinition Orders() =>
        Table("orders") with
        {
            PrimaryKey = PrimaryKey("pk_orders", "id"),
            ForeignKeys =
            [
                new ForeignKeyDefinition
                {
                    Ref = ObjectRef.SubObject("foreignkey", "public", "orders", "fk_orders_customer"),
                    Name = "fk_orders_customer",
                    TableRef = ObjectRef.Table("public", "orders"),
                    Columns = ["id"],
                    ReferencedTableRef = ObjectRef.Table("public", "customers"),
                    ReferencedColumns = ["id"],
                },
            ],
        };

    private static TableDefinition Photos() =>
        Table("photos") with
        {
            Columns = [Column("id", "bigint", 1), Column("payload", "bytea", 2)],
            PrimaryKey = PrimaryKey("pk_photos", "id"),
        };

    /// <summary>A table with three columns, the third able to hold a quote: the shape every row test uses.</summary>
    private static TableDefinition Table(string name) =>
        new()
        {
            Ref = ObjectRef.Table("public", name),
            Name = name,
            SchemaRef = ObjectRef.Schema("public"),
            Columns =
            [
                Column("id", "bigint", 1),
                Column("total", "numeric(10,2)", 2),
                Column("note", "text", 3),
            ],
        };

    private static ColumnDefinition Column(string name, string nativeType, int ordinal) =>
        new()
        {
            Name = name,
            Ordinal = ordinal,
            NativeType = new NativeType(nativeType),
            Nullable = true,
        };

    private static PrimaryKeyDefinition PrimaryKey(string name, params string[] columns) =>
        new()
        {
            Name = name,
            Columns = columns,
        };

    /// <summary>A unique constraint on the fixture's <c>tags</c> table.</summary>
    private static UniqueConstraintDefinition UniqueConstraint(string name, params string[] columns) =>
        new()
        {
            Ref = ObjectRef.SubObject("unique", "public", "tags", name),
            Name = name,
            TableRef = ObjectRef.Table("public", "tags"),
            Columns = columns,
        };

    /// <summary>One stored row of the three-column fixture table.</summary>
    private static DataValue[] Row(long id, string? total, string? note) =>
    [
        DataValue.FromText(ELogicalType.Integer64, id.ToString()),
        total is null ? DataValue.Null(ELogicalType.Decimal) : DataValue.FromText(ELogicalType.Decimal, total),
        note is null ? DataValue.Null(ELogicalType.String) : DataValue.FromText(ELogicalType.String, note),
    ];

    private static ViewDefinition View(string schema, string name, bool materialized) =>
        new()
        {
            Ref = ObjectRef.View(schema, name),
            Name = name,
            SchemaRef = ObjectRef.Schema(schema),
            Definition = new DatabaseExpression(DialectName, "SELECT 1"),
            IsMaterialized = materialized,
        };

    private static NativeObjectDefinition Type(string name, EObjectKind kind, string statement) =>
        new()
        {
            Ref = ObjectRef.Type("public", name),
            Kind = kind,
            Name = name,
            SchemaName = "public",
            NativeDefinition = new NativeDefinition(DialectName, statement),
        };

    /// <summary>The objects one stage is handed, as the plan hands them: the tables serve three stages.</summary>
    private static IReadOnlyList<ISchemaObject> ObjectsFor(SchemaStage stage) =>
        stage switch
        {
            SchemaStage.Schemas =>
            [
                new SchemaDefinition { Ref = ObjectRef.Schema("sales"), Name = "sales" },
            ],
            SchemaStage.Types =>
            [
                Type("mood", EObjectKind.Enum, "CREATE TYPE public.mood AS ENUM ('sad', 'happy');"),
            ],
            SchemaStage.Sequences =>
            [
                new SequenceDefinition
                {
                    Ref = ObjectRef.Sequence("public", "order_seq"),
                    Name = "order_seq",
                    SchemaRef = ObjectRef.Schema("public"),
                    DataType = "bigint",
                },
            ],
            SchemaStage.Tables => [Customers()],
            SchemaStage.Routines =>
            [
                new RoutineDefinition
                {
                    Ref = ObjectRef.Routine("public", "fn_notify"),
                    Name = "fn_notify",
                    SchemaRef = ObjectRef.Schema("public"),
                    NativeDefinition = new NativeDefinition(
                        DialectName,
                        "CREATE OR REPLACE FUNCTION public.fn_notify() RETURNS trigger "
                        + "LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;"),
                },
            ],
            SchemaStage.Views => [View("public", "v_active", materialized: false)],
            SchemaStage.Constraints => [Orders()],
            SchemaStage.Indexes => [Customers()],
            SchemaStage.Triggers =>
            [
                new TriggerDefinition
                {
                    Ref = ObjectRef.SubObject("trigger", "public", "customers", "tr_audit"),
                    Name = "tr_audit",
                    TableRef = ObjectRef.Table("public", "customers"),
                    NativeDefinition = new NativeDefinition(
                        DialectName,
                        "CREATE TRIGGER tr_audit BEFORE INSERT OR UPDATE ON public.customers "
                        + "FOR EACH ROW EXECUTE FUNCTION public.fn_notify();"),
                },
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(stage),
                stage,
                "No object of this fixture belongs to that stage."),
        };

    /// <summary>
    /// The table the index and constraint stages are handed, carrying one hand-made index beside the one
    /// its primary key creates.
    /// </summary>
    private static TableDefinition Customers() =>
        Table("customers") with
        {
            PrimaryKey = PrimaryKey("pk_customers", "id"),
            Indexes =
            [
                new IndexDefinition
                {
                    Ref = ObjectRef.SubObject("index", "public", "customers", "ix_customers_name"),
                    Name = "ix_customers_name",
                    TableRef = ObjectRef.Table("public", "customers"),
                    NativeDefinition = new NativeDefinition(
                        DialectName,
                        "CREATE INDEX ix_customers_name ON public.customers (note)"),
                },
            ],
        };

    /// <summary>A binary value held outside the row file, as the interchange reader hands one over.</summary>
    private sealed class StoredPayload(byte[] bytes) : IBinaryPayload
    {
        public long Length => bytes.LongLength;

        public Stream OpenRead() => new MemoryStream(bytes, writable: false);
    }
}
