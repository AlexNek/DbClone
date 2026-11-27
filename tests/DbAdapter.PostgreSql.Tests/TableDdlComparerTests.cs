using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Compare;
using FluentAssertions;
using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Tests;

public class TableDdlComparerTests
{
    private readonly TableDdlComparer _sut = new();

    // ─────────────────────────────────────────────────────────────────────────
    // NormalizeCheckExpression
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CHECK ((a IS NOT NULL))", "a IS NOT NULL")]
    [InlineData("CHECK (a IS NOT NULL)", "a IS NOT NULL")]
    [InlineData("CHECK (((x > 0)))", "x > 0")]
    [InlineData("  CHECK  ( ( a  AND  b ) )  ", "a AND b")]
    [InlineData("(a) AND (b)", "(a) AND (b)")] // not a single outer wrap
    [InlineData("CHECK ((a) AND (b))", "(a) AND (b)")] // outer stripped, inner kept
    public void NormalizeCheckExpression_StripsRedundantParensAndWhitespace(
        string input, string expected)
    {
        TableDdlComparer.NormalizeCheckExpression(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeCheckExpression_EquivalentExpressions_Match()
    {
        // Simulates pg_get_constraintdef returning different parenthesization.
        var src = "CHECK ((payload IS NOT NULL))";
        var dst = "CHECK (payload IS NOT NULL)";

        TableDdlComparer.NormalizeCheckExpression(src)
            .Should().Be(TableDdlComparer.NormalizeCheckExpression(dst));
    }

    [Fact]
    public void NormalizeCheckExpression_ArrayCastVariants_AreEquivalent()
    {
        // PostgreSQL decompiler variance: whole-array cast vs per-element cast.
        // Source (older PG style): casts entire ARRAY to text[]
        var src = "CHECK (((stat_type)::text = ANY ((ARRAY['int'::character varying, 'double'::character varying, 'text'::character varying])::text[])))";
        // Dest (newer PG style): casts each element individually to text
        var dst = "CHECK (((stat_type)::text = ANY (ARRAY[('int'::character varying)::text, ('double'::character varying)::text, ('text'::character varying)::text])))";

        var srcNorm = TableDdlComparer.NormalizeCheckExpression(src);
        var dstNorm = TableDdlComparer.NormalizeCheckExpression(dst);

        srcNorm.Should().Be(dstNorm,
            "both forms represent the same CHECK constraint — " +
            "PostgreSQL just decompiles the type cast differently across versions");
    }

    [Fact]
    public void NormalizeCheckExpression_ArrayCast_RealDifference_StillDetected()
    {
        // Same cast pattern but different enum values — this is a REAL difference.
        var src = "CHECK (((stat_type)::text = ANY ((ARRAY['int'::character varying, 'double'::character varying])::text[])))";
        var dst = "CHECK (((stat_type)::text = ANY ((ARRAY['int'::character varying, 'text'::character varying])::text[])))";

        var srcNorm = TableDdlComparer.NormalizeCheckExpression(src);
        var dstNorm = TableDdlComparer.NormalizeCheckExpression(dst);

        srcNorm.Should().NotBe(dstNorm,
            "the actual constraint values differ — 'double' vs 'text'");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Compare — CHECK constraints
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_IdenticalCheck_NoDifference()
    {
        var model = ModelWithTable(
            checks: [new CheckConstraintDefinition("chk1", "CHECK ((x > 0))", false, false)]);

        var items = _sut.Compare(model, model, CancellationToken.None);

        items.Should().BeEmpty();
    }

    [Fact]
    public void Compare_NormalizedEquivalentCheck_NoDifference()
    {
        var source = ModelWithTable(
            checks: [new CheckConstraintDefinition("chk1", "CHECK ((x > 0))", false, false)]);
        var dest = ModelWithTable(
            checks: [new CheckConstraintDefinition("chk1", "CHECK (x > 0)", false, false)]);

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().BeEmpty();
    }

    [Fact]
    public void Compare_RealCheckDifference_IncludesBothExpressions()
    {
        var source = ModelWithTable(
            checks: [new CheckConstraintDefinition("chk1", "CHECK ((x > 0))", false, false)]);
        var dest = ModelWithTable(
            checks: [new CheckConstraintDefinition("chk1", "CHECK ((x > 10))", false, false)]);

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Different);
        items[0].Details.Should().Contain("source: CHECK ((x > 0))");
        items[0].Details.Should().Contain("dest: CHECK ((x > 10))");
    }

    [Fact]
    public void Compare_PartitionChild_CheckDifferenceDowngradedToNotice()
    {
        var source = ModelWithTable(
            parentTable: "messages",
            checks: [new CheckConstraintDefinition("chk1", "CHECK ((x > 0))", false, false)]);
        var dest = ModelWithTable(
            parentTable: "messages",
            checks: [new CheckConstraintDefinition("chk1", "CHECK ((x > 10))", false, false)]);

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Notice);
        items[0].Details.Should().StartWith("DDL notice:");
        items[0].Details.Should().Contain("CHECK modified: chk1");
    }

    [Fact]
    public void Compare_PartitionChild_NormalizedEquivalent_NoItem()
    {
        var source = ModelWithTable(
            parentTable: "messages",
            checks: [new CheckConstraintDefinition("chk1", "CHECK ((x > 0))", false, false)]);
        var dest = ModelWithTable(
            parentTable: "messages",
            checks: [new CheckConstraintDefinition("chk1", "CHECK ( x > 0 )", false, false)]);

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().BeEmpty();
    }

    [Fact]
    public void Compare_CheckRemoved_NonPartition_IsDifferent()
    {
        var source = ModelWithTable(
            checks: [new CheckConstraintDefinition("chk1", "CHECK (x > 0)", false, false)]);
        var dest = ModelWithTable(checks: []);

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Different);
        items[0].Details.Should().Contain("CHECK removed: chk1");
    }

    [Fact]
    public void Compare_CheckRemoved_PartitionChild_IsNotice()
    {
        var source = ModelWithTable(
            parentTable: "parent_tbl",
            checks: [new CheckConstraintDefinition("chk1", "CHECK (x > 0)", false, false)]);
        var dest = ModelWithTable(parentTable: "parent_tbl", checks: []);

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Notice);
        items[0].Details.Should().Contain("inherited from parent");
    }

    [Fact]
    public void Compare_MixedHardAndNotice_ReportsDifferentWithAllDetails()
    {
        // A non-check hard difference (column removed) + partition CHECK notice.
        var srcTable = new TableDefinition(
            "public", "tbl",
            Columns: [Col("id"), Col("val", "text", 2)],
            Indexes: [], ForeignKeys: [],
            CheckConstraints: [new CheckConstraintDefinition("chk1", "CHECK ((x > 0))", false, false)],
            UniqueConstraints: [],
            Comment: null, IsPartitioned: false, PartitionStrategy: null,
            ParentTable: "parent_tbl");
        var dstTable = new TableDefinition(
            "public", "tbl",
            Columns: [Col("id")],
            Indexes: [], ForeignKeys: [],
            CheckConstraints: [new CheckConstraintDefinition("chk1", "CHECK ((x > 99))", false, false)],
            UniqueConstraints: [],
            Comment: null, IsPartitioned: false, PartitionStrategy: null,
            ParentTable: "parent_tbl");

        var source = Model(srcTable);
        var dest = Model(dstTable);

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Different);
        items[0].Details.Should().Contain("Columns removed: val");
        items[0].Details.Should().Contain("CHECK modified: chk1");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Compare — foreign key scope
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_FkParentOutsideBothModels_IsNotice()
    {
        // The parent lives in a schema neither read covered: the constraint could not be
        // compared, so its absence on the destination is not a difference.
        var source = Model(
            FkTable("public", "comments", Fk("comments_created_by_fkey", "auth", "users")));
        var dest = Model(FkTable("public", "comments"));

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Notice);
        items[0].Details.Should().Contain(
            "FK not compared: comments_created_by_fkey (parent auth.users is outside the compared scope)");
        items[0].Details.Should().NotContain("FK removed");
    }

    [Fact]
    public void Compare_FkParentInsideBothModels_RemovedIsStillDifferent()
    {
        var source = Model(
            FkTable("public", "users"),
            FkTable("public", "orders", Fk("orders_owner_id_fkey", "public", "users")));
        var dest = Model(
            FkTable("public", "users"),
            FkTable("public", "orders"));

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Different);
        items[0].Details.Should().Contain("FK removed: orders_owner_id_fkey");
    }

    [Fact]
    public void Compare_FkParentOnSourceOnly_IsNoticeNotRemoved()
    {
        var source = Model(
            FkTable("public", "users"),
            FkTable("public", "orders", Fk("orders_owner_id_fkey", "public", "users")));
        var dest = Model(FkTable("public", "orders"));

        var items = _sut.Compare(source, dest, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Notice);
        items[0].Details.Should().Contain("FK not compared: orders_owner_id_fkey");
        items[0].Details.Should().NotContain("FK removed");
    }

    [Fact]
    public void Compare_RealDifferenceAndOutOfScopeFk_StaysDifferentAndListsBoth()
    {
        var srcTable = new TableDefinition(
            "public", "orders",
            Columns: [Col("id"), Col("val", "text", 2)],
            Indexes: [],
            ForeignKeys: [Fk("orders_created_by_fkey", "auth", "users")],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null, IsPartitioned: false, PartitionStrategy: null,
            ParentTable: null);
        var dstTable = new TableDefinition(
            "public", "orders",
            Columns: [Col("id")],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null, IsPartitioned: false, PartitionStrategy: null,
            ParentTable: null);

        var items = _sut.Compare(Model(srcTable), Model(dstTable), CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Status.Should().Be(ECompareStatus.Different);
        items[0].Details.Should().Contain("Columns removed: val");
        items[0].Details.Should().Contain("FK not compared: orders_created_by_fkey");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static ColumnDefinition Col(string name, string type = "integer", int ordinal = 1) =>
        new(name, type, ordinal, false, null, false, false, null, null);

    private static ForeignKeyDefinition Fk(string name, string parentSchema, string parentTable) =>
        new(name, ["owner_id"], parentSchema, parentTable, ["id"], "NO ACTION", "NO ACTION", false, false);

    private static TableDefinition FkTable(
        string schema, string name, params ForeignKeyDefinition[] foreignKeys) =>
        new(schema, name, [Col("id")], [], foreignKeys, [], [], null, false, null, null);

    private static DatabaseModel Model(params TableDefinition[] tables) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "16.0",
            Schemas: [],
            Tables: tables,
            Views: [],
            MaterializedViews: [],
            Sequences: [],
            Enums: [],
            Domains: [],
            CompositeTypes: [],
            Functions: [],
            Triggers: [],
            Policies: [],
            Publications: [],
            Subscriptions: [],
            Extensions: []);

    private static DatabaseModel ModelWithTable(
        string? parentTable = null,
        IReadOnlyList<CheckConstraintDefinition>? checks = null)
    {
        var table = new TableDefinition(
            SchemaName: "public",
            Name: "test_table",
            Columns: [Col("id")],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: checks ?? [],
            UniqueConstraints: [],
            Comment: null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: parentTable);

        return Model(table);
    }
}
