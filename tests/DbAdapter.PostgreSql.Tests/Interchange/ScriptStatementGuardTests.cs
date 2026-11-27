using CanonicalModel.Interchange;

using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Interchange;

using FluentAssertions;

using Xunit;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// D3's table, one test per row: the text the restore executes, and the text a script writes in its place so
/// the file may be run against a database that already holds what the same file created.
/// </summary>
/// <remarks>
/// The statements below are the shapes <see cref="PostgreSqlWriter"/> really produces, because the guard is
/// applied to that text and never to a second builder's. The one case that cannot be written by hand — a
/// recorded check constraint whose expression still carries <c>NOT VALID</c> — is asked of the writer itself.
/// </remarks>
public class ScriptStatementGuardTests
{
    [Fact]
    public void Guard_Schema_AddsIfNotExists()
    {
        var guarded = ScriptStatementGuard.Guard("CREATE SCHEMA sales;");

        guarded.Should().Equal("CREATE SCHEMA IF NOT EXISTS sales;");
    }

    [Fact]
    public void Guard_Schema_WhichAlreadyCarriesTheKeyword_IsUnchanged()
    {
        // The generator the copy pipeline shares already writes the re-runnable form, so adding the
        // keyword again would be a syntax error rather than a second guard.
        var guarded = ScriptStatementGuard.Guard("CREATE SCHEMA IF NOT EXISTS sales;");

        guarded.Should().Equal("CREATE SCHEMA IF NOT EXISTS sales;");
    }

    [Fact]
    public void Guard_Type_IsWrappedInABlockThatTreatsADuplicateAsDone()
    {
        // A type has no IF NOT EXISTS, and its name is the only thing that could collide.
        var guarded = ScriptStatementGuard.Guard("CREATE TYPE \"public\".\"mood\" AS ENUM ('sad', 'happy');");

        guarded.Should().ContainSingle();
        guarded[0].Should().StartWith("DO $dbclone$\nBEGIN\n    CREATE TYPE");
        guarded[0].Should().Contain("WHEN duplicate_object OR duplicate_table THEN NULL;");
        guarded[0].Should().EndWith("END\n$dbclone$;");
    }

    [Fact]
    public void Guard_TypeWhoseBodyCarriesTheWrapperTag_TakesATagTheBodyDoesNotHold()
    {
        // A tag the body already contains would close the block where the body starts, so the wrapper takes
        // the first tag the statement's own text does not carry — and the choice depends on that text alone,
        // which keeps the file byte-identical from one store to the next (FR11).
        var guarded = ScriptStatementGuard.Guard(
            "CREATE TYPE \"public\".\"mood\" AS ENUM ('$dbclone$', 'happy');");

        guarded.Should().ContainSingle();
        guarded[0].Should().StartWith("DO $dbclone2$\nBEGIN\n    CREATE TYPE");
        guarded[0].Should().Contain("'$dbclone$'");
        guarded[0].Should().EndWith("END\n$dbclone2$;");
    }

    [Fact]
    public void Guard_LineBreakInsideALiteral_IsKeptBecauseItIsPartOfTheValue()
    {
        // FR11 puts every line break of the file in its \n form, but a break inside a quoted literal or a
        // dollar-quoted body is part of a value: rewriting it would change what the script says the database
        // holds, and the check would then fail or pass where the source did the opposite.
        var guarded = ScriptStatementGuard.Guard(
            "CREATE DOMAIN \"public\".\"two_lines\" AS text CHECK ((VALUE <> 'a\r\nb'));");

        guarded.Should().ContainSingle();
        guarded[0].Should().Contain("'a\r\nb'");
    }

    [Fact]
    public void Guard_LineBreakOutsideALiteral_IsWrittenAsOneLineBreak()
    {
        // The statement's own formatting is the file's, so it is normalized even when a literal beside it
        // keeps its break untouched.
        var guarded = ScriptStatementGuard.Guard(
            "CREATE VIEW \"public\".\"v\" AS\r\nSELECT 'a\r\nb'\r\nFROM t;");

        guarded.Should().ContainSingle();
        guarded[0].Should().Be("CREATE OR REPLACE VIEW \"public\".\"v\" AS\nSELECT 'a\r\nb'\nFROM t;");
    }

    [Fact]
    public void Guard_Domain_IsWrappedInTheSameBlock()
    {
        var guarded = ScriptStatementGuard.Guard(
            "CREATE DOMAIN \"public\".\"positive_amount\" AS numeric CHECK ((VALUE > 0));");

        guarded[0].Should().StartWith("DO $dbclone$");
        guarded[0].Should().Contain("CREATE DOMAIN");
    }

    [Fact]
    public void Guard_Routine_IsPassedThrough_BecauseTheEngineAlreadyReplacesIt()
    {
        // A routine arrives as CREATE OR REPLACE FUNCTION, which is the engine's own re-runnable form. The
        // body is a dollar-quoted block the guard must not read as SQL.
        const string Routine =
            "CREATE OR REPLACE FUNCTION \"public\".\"fn_notify\"() RETURNS trigger LANGUAGE plpgsql AS "
            + "$$ BEGIN RETURN NEW; END $$;";

        ScriptStatementGuard.Guard(Routine).Should().Equal(Routine);
    }

    [Fact]
    public void Guard_Sequence_AddsIfNotExists()
    {
        var guarded = ScriptStatementGuard.Guard(
            "CREATE SEQUENCE \"public\".\"order_seq\" AS bigint INCREMENT BY 1 START WITH 1 NO MINVALUE CACHE 1;");

        guarded.Should().Equal(
            "CREATE SEQUENCE IF NOT EXISTS \"public\".\"order_seq\" AS bigint INCREMENT BY 1 START WITH 1 "
            + "NO MINVALUE CACHE 1;");
    }

    [Fact]
    public void Guard_Table_AddsIfNotExists_AndWritesOneLineBreak()
    {
        // The writer separates the column declarations with the platform's own line break; the file gets \n.
        var table = "CREATE TABLE \"public\".\"orders\" (\r\n    \"id\" bigint NOT NULL,\r\n    \"total\" numeric\r\n);";

        var guarded = ScriptStatementGuard.Guard(table);

        guarded.Should().ContainSingle();
        guarded[0].Should().StartWith("CREATE TABLE IF NOT EXISTS \"public\".\"orders\" (");
        guarded[0].Should().NotContain("\r");
        guarded[0].Should().EndWith(");");
    }

    [Fact]
    public void Guard_Partition_AddsIfNotExists_InTheSamePlace()
    {
        // A partition is created by the same verb, and the keyword stands in the same position.
        var guarded = ScriptStatementGuard.Guard(
            "CREATE TABLE \"public\".\"orders_2026\" PARTITION OF \"public\".\"orders\" FOR VALUES FROM (1) TO (2);");

        guarded.Should().Equal(
            "CREATE TABLE IF NOT EXISTS \"public\".\"orders_2026\" PARTITION OF \"public\".\"orders\" "
            + "FOR VALUES FROM (1) TO (2);");
    }

    [Fact]
    public void Guard_View_AddsOrReplace_WhichCoversAnUnchangedColumnList()
    {
        var guarded = ScriptStatementGuard.Guard(
            "CREATE VIEW \"public\".\"v_active\" AS\nSELECT 1;");

        guarded.Should().Equal("CREATE OR REPLACE VIEW \"public\".\"v_active\" AS\nSELECT 1;");
    }

    [Fact]
    public void Guard_View_WhichTheGeneratorAlreadyReplaces_IsUnchanged()
    {
        const string View = "CREATE OR REPLACE VIEW \"public\".\"v_active\" AS\nSELECT 1;";

        ScriptStatementGuard.Guard(View).Should().Equal(View);
    }

    [Fact]
    public void Guard_MaterializedView_AddsIfNotExists()
    {
        var guarded = ScriptStatementGuard.Guard(
            "CREATE MATERIALIZED VIEW \"reporting\".\"mv_totals\" AS\nSELECT 1 WITH NO DATA;");

        guarded.Should().Equal(
            "CREATE MATERIALIZED VIEW IF NOT EXISTS \"reporting\".\"mv_totals\" AS\nSELECT 1 WITH NO DATA;");
    }

    [Fact]
    public void Guard_Index_AddsIfNotExists_ForThePlainAndTheUniqueForm()
    {
        ScriptStatementGuard.Guard("CREATE INDEX \"ix_name\" ON \"public\".\"customers\" (\"name\");")
            .Should().Equal("CREATE INDEX IF NOT EXISTS \"ix_name\" ON \"public\".\"customers\" (\"name\");");

        ScriptStatementGuard.Guard("CREATE UNIQUE INDEX \"pk_orders\" ON \"public\".\"orders\" (\"id\");")
            .Should().Equal("CREATE UNIQUE INDEX IF NOT EXISTS \"pk_orders\" ON \"public\".\"orders\" (\"id\");");
    }

    [Fact]
    public void Guard_ForeignKey_DropsTheOldOneAndAddsItUnvalidated()
    {
        // There is no IF NOT EXISTS for a constraint, so the name is dropped first and the new one is added
        // NOT VALID: a re-run against a target that already holds rows fails on nothing until the tail
        // validates it.
        var foreignKey = "ALTER TABLE \"public\".\"orders\" ADD CONSTRAINT \"fk_orders_customer\" "
            + "FOREIGN KEY (\"customer_id\") REFERENCES \"public\".\"customers\" (\"id\") ON DELETE CASCADE;";

        ScriptStatementGuard.Guard(foreignKey).Should().Equal(
            "ALTER TABLE \"public\".\"orders\" DROP CONSTRAINT IF EXISTS \"fk_orders_customer\";",
            "ALTER TABLE \"public\".\"orders\" ADD CONSTRAINT \"fk_orders_customer\" "
            + "FOREIGN KEY (\"customer_id\") REFERENCES \"public\".\"customers\" (\"id\") "
            + "ON DELETE CASCADE NOT VALID;");
    }

    [Fact]
    public void Guard_Trigger_DropsItFirst_AndKeepsTheCapturedText()
    {
        // CREATE OR REPLACE TRIGGER exists only from PostgreSQL 14, and dropping a trigger destroys no data.
        const string Trigger =
            "CREATE TRIGGER tr_audit BEFORE INSERT OR UPDATE ON public.customers FOR EACH ROW "
            + "WHEN (NEW.id IS NOT NULL) EXECUTE FUNCTION public.fn_notify();";

        ScriptStatementGuard.Guard(Trigger).Should().Equal(
            "DROP TRIGGER IF EXISTS tr_audit ON public.customers;",
            Trigger);
    }

    [Fact]
    public void Guard_TriggerAddedAsAConstraintTrigger_FindsTheTableAfterTheName()
    {
        const string Trigger =
            "CREATE CONSTRAINT TRIGGER \"tr_deferred\" AFTER INSERT ON \"public\".\"orders\" "
            + "DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION \"public\".\"fn_notify\"();";

        var guarded = ScriptStatementGuard.Guard(Trigger);

        guarded[0].Should().Be("DROP TRIGGER IF EXISTS \"tr_deferred\" ON \"public\".\"orders\";");
        guarded[1].Should().Be(Trigger);
    }

    [Fact]
    public void Guard_DisabledTrigger_SwitchesItOffAfterCreatingIt()
    {
        // The stage hands over the DISABLE TRIGGER of a trigger the source had switched off. It names the
        // trigger the same file created, so there is nothing to guard and nothing to reorder.
        const string Disable = "ALTER TABLE \"public\".\"orders\" DISABLE TRIGGER \"tr_disabled\";";

        ScriptStatementGuard.Guard(Disable).Should().Equal(Disable);
    }

    [Fact]
    public void Guard_TableWithARecordedNotValidCheck_StripsTheClauseTheWriterCannotPutInCreateTable()
    {
        // pg_get_constraintdef appends " NOT VALID" for a constraint the source never validated, and that
        // clause is legal only in ALTER TABLE. The writer strips it; the guard must not put it back, and the
        // statement must still be re-runnable.
        var table = new TableDefinition
        {
            Ref = ObjectRef.Table("public", "orders"),
            Name = "orders",
            SchemaRef = ObjectRef.Schema("public"),
            Columns =
            [
                new ColumnDefinition
                {
                    Ref = ObjectRef.Column("public", "orders", "total"),
                    Name = "total",
                    TableRef = ObjectRef.Table("public", "orders"),
                    Ordinal = 1,
                    Nullable = true,
                    NativeType = new NativeType("numeric"),
                },
            ],
            CheckConstraints =
            [
                new CheckConstraintDefinition
                {
                    Ref = ObjectRef.SubObject("constraint", "public", "orders", "chk_total"),
                    Name = "chk_total",
                    TableRef = ObjectRef.Table("public", "orders"),
                    Expression = new DatabaseExpression("postgresql", "(total >= 0) NOT VALID"),
                },
            ],
        };

        var built = PostgreSqlWriter.BuildStatements("postgresql", SchemaStage.Tables, [table]);
        var guarded = built.SelectMany(ScriptStatementGuard.Guard).ToList();

        guarded.Should().ContainSingle();
        guarded[0].Should().StartWith("CREATE TABLE IF NOT EXISTS public.orders (");
        guarded[0].Should().Contain("chk_total");
        guarded[0].Should().NotContain("NOT VALID");
    }

    [Fact]
    public void Guard_StatementOfAnUnknownClass_IsRefused()
    {
        // An unguarded statement in the file would make a second run report as applied while it failed, so
        // the class that is not in the table is refused rather than written unchanged.
        var act = () => ScriptStatementGuard.Guard("GRANT SELECT ON ALL TABLES IN SCHEMA public TO test_user;");

        act.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("GRANT SELECT");
    }
}
