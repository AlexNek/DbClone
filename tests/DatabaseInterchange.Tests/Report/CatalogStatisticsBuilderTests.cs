using CanonicalModel.Interchange;

using DatabaseInterchange.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// The statistics builder turns the writer's write-time facts into the account the report prints: per
/// table, per schema and in total. The measures are asserted to stay distinct — file counts and byte
/// sums answer different questions — because the document prints them in different columns (D3, FR3).
/// </summary>
public sealed class CatalogStatisticsBuilderTests
{
    private readonly CatalogStatisticsBuilder _builder = new();

    [Fact]
    public void Build_SumsRowsChunksAndBytesPerTable()
    {
        // Arrange
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "orders"),
            ReportModel.KeyedTable("public", "invoices"));
        var written = new[]
        {
            ReportModel.Written("public", "orders", rows: 120, chunks: 3, rowBytes: 4000, blobFiles: 2, blobBytes: 900),
            ReportModel.Written("public", "invoices", rows: 7, chunks: 1, rowBytes: 100),
        };

        // Act
        var statistics = _builder.Build(
            snapshot, written, [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert
        statistics.Rows.Should().Be(127);
        statistics.ChunkFiles.Should().Be(4);
        statistics.RowBytes.Should().Be(4100);
        statistics.BlobFiles.Should().Be(2);
        statistics.BlobBytes.Should().Be(900);
        statistics.DataBytes.Should().Be(4100 + 900);

        var orders = statistics.Tables.Single(t => t.Name == "orders");
        orders.Rows.Should().Be(120);
        orders.Chunks.Should().Be(3);
        orders.DataBytes.Should().Be(4900);
    }

    [Fact]
    public void Build_RollsTablesUpPerSchema()
    {
        // Arrange
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "orders"),
            ReportModel.KeyedTable("public", "customers"),
            ReportModel.KeyedTable("billing", "invoices"));
        var written = new[]
        {
            ReportModel.Written("public", "orders", rows: 10, rowBytes: 600),
            ReportModel.Written("public", "customers", rows: 4, rowBytes: 300),
            ReportModel.Written("billing", "invoices", rows: 2, rowBytes: 100),
        };

        // Act
        var statistics = _builder.Build(
            snapshot, written, [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert
        statistics.Schemas.Select(s => s.Name).Should().Equal("billing", "public");
        var publicSchema = statistics.Schemas.Single(s => s.Name == "public");
        publicSchema.Tables.Should().Be(2);
        publicSchema.Rows.Should().Be(14);
        publicSchema.DataBytes.Should().Be(900);
    }

    [Fact]
    public void Build_CountsEveryCapturedKindAndKeepsTheDataHalfSeparate()
    {
        // Arrange
        var snapshot = ReportModel.Snapshot(ReportModel.KeyedTable("public", "orders")) with
        {
            Views =
            [
                new ViewDefinition
                {
                    Ref = ObjectRef.View("public", "active_orders"),
                    Name = "active_orders",
                    SchemaRef = ObjectRef.Schema("public"),
                },
                new ViewDefinition
                {
                    Ref = ObjectRef.View("public", "orders_by_day"),
                    Name = "orders_by_day",
                    SchemaRef = ObjectRef.Schema("public"),
                    IsMaterialized = true,
                },
            ],
            Relationships =
            [
                new Relationship
                {
                    DependentRef = ObjectRef.View("public", "active_orders"),
                    DependencyRef = ObjectRef.Table("public", "orders"),
                    Type = "viewReference",
                },
            ],
            Security = new SecuritySection
            {
                Roles = [Role("test_user")],
                Grants = [Grant("test_user", "public", "orders")],
                Policies = [],
            },
        };

        // Act
        var statistics = _builder.Build(
            snapshot, [], [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert
        statistics.SchemaCount.Should().Be(1);
        statistics.TableCount.Should().Be(1);
        statistics.ColumnCount.Should().Be(2);
        statistics.ViewCount.Should().Be(1);
        statistics.MaterializedViewCount.Should().Be(1);
        statistics.ViewReferenceCount.Should().Be(1);
        statistics.RoleCount.Should().Be(1);
        statistics.GrantCount.Should().Be(1);
        statistics.PolicyCount.Should().Be(0);

        // A capture that wrote no rows is a schema-only store, not a store of empty tables (FR10).
        statistics.HasData.Should().BeFalse();
        statistics.EmptyTableCount.Should().Be(0);
    }

    [Fact]
    public void Build_CountsEmptyTablesFromRowsNotFromAMissingChunkFile()
    {
        // Arrange — a table with no rows yields no chunk file at all; an empty blob container does exist.
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "with_rows"),
            ReportModel.KeyedTable("public", "empty_table"));
        var written = new[]
        {
            ReportModel.Written("public", "with_rows", rows: 5, chunks: 1, rowBytes: 200),
            ReportModel.Written("public", "empty_table", rows: 0, chunks: 0, rowBytes: 0, blobFiles: 1, blobBytes: 0),
        };

        // Act
        var statistics = _builder.Build(
            snapshot, written, [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert — file counts and byte sums are different measures of the same folder (D3).
        statistics.EmptyTableCount.Should().Be(1);
        statistics.BlobFiles.Should().Be(1);
        statistics.BlobBytes.Should().Be(0);
        statistics.ChunkFiles.Should().Be(1);
        statistics.DataBytes.Should().Be(200);
    }

    [Fact]
    public void Build_KeepsATableTheDataPathSkippedInTheListingWithZeroData()
    {
        // Arrange — FR10 prints every captured table, so an excluded one must not vanish.
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "kept"),
            ReportModel.KeyedTable("public", "excluded"));
        var written = new[] { ReportModel.Written("public", "kept", rows: 9, chunks: 1, rowBytes: 300) };

        // Act
        var statistics = _builder.Build(
            snapshot, written, [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", excludedTableCount: 1, []);

        // Assert
        statistics.Tables.Select(t => t.Name).Should().Equal("excluded", "kept");
        var skipped = statistics.Tables.Single(t => t.Name == "excluded");
        skipped.Rows.Should().Be(0);
        skipped.Chunks.Should().Be(0);
        skipped.DataBytes.Should().Be(0);
        skipped.HasPrimaryKey.Should().BeTrue("the schema facts are captured even when no rows were read");
        statistics.ExcludedTableCount.Should().Be(1);
    }

    [Fact]
    public void Build_NamesTheLargestTableAndBreaksTiesOnSchemaAndTableOrdinal()
    {
        // Arrange
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "b_same"),
            ReportModel.KeyedTable("public", "a_same"),
            ReportModel.KeyedTable("public", "big"));
        var written = new[]
        {
            ReportModel.Written("public", "b_same", rows: 1, rowBytes: 500),
            ReportModel.Written("public", "a_same", rows: 1, rowBytes: 500),
            ReportModel.Written("public", "big", rows: 1, rowBytes: 5000),
        };

        // Act
        var statistics = _builder.Build(
            snapshot, written, [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert
        statistics.LargestTable!.QualifiedName.Should().Be("public.big");

        var tieBreak = _builder.Build(
            snapshot,
            [
                ReportModel.Written("public", "b_same", rows: 1, rowBytes: 500),
                ReportModel.Written("public", "a_same", rows: 1, rowBytes: 500),
            ],
            [],
            0,
            ["tables"],
            ReportModel.ExportedAt,
            ReportModel.Dialect,
            "consistent snapshot",
            0,
            []);
        tieBreak.LargestTable!.QualifiedName.Should().Be("public.a_same");
    }

    [Fact]
    public void Build_SumsTheTopTablesShareWithoutCountingMoreThanTheBudgetAllows()
    {
        // Arrange — eleven tables with descending byte counts, so the eleventh must fall outside.
        var snapshot = ReportModel.WideSchema(11);
        var written = snapshot.Tables
            .Select((table, index) => ReportModel.Written(
                "public", table.Name, rows: 1, rowBytes: (11 - index) * 100L))
            .ToArray();

        // Act
        var statistics = _builder.Build(
            snapshot, written, [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert — eleven tables, so the smallest of them falls outside the ten the prose sums.
        statistics.TopTablesDataBytes.Should().Be(Enumerable.Range(2, 10).Sum(size => size * 100L));
    }

    [Fact]
    public void Build_KeepsTheSourceOrderListAndMarksOnlyThoseTables()
    {
        // Arrange — the written list is the only source for the mark (FR4).
        var snapshot = ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "audit_log"),
            ReportModel.KeyedTable("public", "session_log"),
            ReportModel.KeyedTable("public", "customers"));
        var sourceOrder = new[]
        {
            new DbAdapter.Definitions.TableRef("public", "session_log"),
            new DbAdapter.Definitions.TableRef("public", "audit_log"),
        };

        // Act
        var statistics = _builder.Build(
            snapshot, ReportModel.WrittenEveryTable(snapshot), [], 0, ["tables"],
            ReportModel.ExportedAt, ReportModel.Dialect, "consistent snapshot", 0, sourceOrder);

        // Assert — sorted ordinal, so the list is stable whatever order the exporter reported in.
        statistics.SourceOrderTables.Should().Equal("public.audit_log", "public.session_log");
        statistics.Tables.Single(t => t.Name == "audit_log").IsSourceOrder.Should().BeTrue();
        statistics.Tables.Single(t => t.Name == "customers").IsSourceOrder.Should().BeFalse();
    }

    [Fact]
    public void Build_SpellsRowOrderAndKeysForReading()
    {
        // Arrange
        var orders = ReportModel.Table("public", "orders",
            ReportModel.Column("public", "orders", "tenant_id", nullable: false),
            ReportModel.Column("public", "orders", "order_no", nullable: false, nativeType: "varchar"),
            ReportModel.Column("public", "orders", "customer_id", nativeType: "uuid")) with
        {
            PrimaryKey = ReportModel.PrimaryKey("public", "orders", "tenant_id", "order_no"),
            UniqueConstraints = [ReportModel.Unique("public", "orders", "uq_orders_no", "order_no")],
            RowOrder = ERowOrder.SourceOrder,
        };

        // Act
        var statistics = _builder.Build(
            ReportModel.Snapshot(orders), ReportModel.WrittenEveryTable(ReportModel.Snapshot(orders)),
            [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert
        var table = statistics.Tables.Single();
        table.RowOrder.Should().Be("source order");
        table.Keys.Should().Be("(tenant_id, order_no) PK · `order_no` (`varchar`) UK");
    }

    [Fact]
    public void Build_CountsRelationsInBothDirectionsPerTable()
    {
        // Arrange
        var customers = ReportModel.KeyedTable("public", "customers");
        var orders = ReportModel.KeyedTable("public", "orders") with
        {
            ForeignKeys =
            [
                ReportModel.Reference("public", "orders", "fk_orders_customer", "public", "customers", ["customer_id"], ["id"]),
                ReportModel.Reference("public", "orders", "fk_orders_billing", "public", "customers", ["billing_id"], ["id"]),
            ],
        };

        // Act
        var statistics = _builder.Build(
            ReportModel.Snapshot(customers, orders), ReportModel.WrittenEveryTable(ReportModel.Snapshot(customers, orders)),
            [], 0, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert — three constraints between one pair are three relations (§5.1).
        statistics.Tables.Single(t => t.Name == "orders").ForeignKeysOut.Should().Be(2);
        statistics.Tables.Single(t => t.Name == "customers").ForeignKeysIn.Should().Be(2);
        statistics.ForeignKeyCount.Should().Be(2);
    }

    [Fact]
    public void Build_ComposesTheThreeByteMeasuresOutOfTheWritersOwnFacts()
    {
        // Arrange
        var snapshot = ReportModel.Snapshot(ReportModel.KeyedTable("public", "orders"));
        var metadata = new[] { ("schema/tables.json", 700L), ("data/public/orders/table.json", 300L) };

        // Act
        var statistics = _builder.Build(
            snapshot,
            [ReportModel.Written("public", "orders", rows: 1, chunks: 1, rowBytes: 250, blobFiles: 1, blobBytes: 1000)],
            metadata,
            rootJsonBytes: 400,
            ["tables"],
            ReportModel.ExportedAt,
            ReportModel.Dialect,
            "consistent snapshot",
            0,
            []);

        // Assert — metadata bytes carry the root files, catalog bytes is the whole artifact (D3).
        statistics.DataBytes.Should().Be(1250);
        statistics.MetadataBytes.Should().Be(1400);
        statistics.CatalogBytes.Should().Be(2650);
        statistics.MetadataFiles.Sum(g => g.TotalBytes).Should().Be(1400);
    }

    [Fact]
    public void Build_GroupsMetadataFilesIntoTheThreeGroupsFr5Names()
    {
        // Arrange
        var snapshot = ReportModel.Snapshot(ReportModel.KeyedTable("public", "orders"));
        var metadata = new[]
        {
            ("schema/tables.json", 100L),
            ("schema/views.json", 50L),
            ("native/postgresql.json", 70L),
            ("data/public/orders/table.json", 30L),
            ("catalog.json", 20L),
        };

        // Act
        var statistics = _builder.Build(
            snapshot, [], metadata, rootJsonBytes: 11, ["tables"], ReportModel.ExportedAt,
            ReportModel.Dialect, "consistent snapshot", 0, []);

        // Assert
        statistics.MetadataFiles.Select(g => g.Label).Should().Equal(
            "schema sections", "engine native", "catalog and per-table metadata");
        statistics.MetadataFiles[0].Files.Select(f => f.Path).Should().Equal(
            "schema/tables.json", "schema/views.json");
        statistics.MetadataFiles[0].TotalBytes.Should().Be(150);
        statistics.MetadataFiles[1].TotalBytes.Should().Be(70);

        // The two root files have no per-file figure, so they read as the one row the writer can state.
        statistics.MetadataFiles[2].Files.Select(f => f.Path).Should().Equal(
            "catalog.json", "data/public/orders/table.json", ReportText.RootMetadataFiles);
        statistics.MetadataFiles[2].TotalBytes.Should().Be(61);
    }

    [Fact]
    public void Build_RejectsAMissingInput()
    {
        var snapshot = ReportModel.Snapshot();

        var act = () => _builder.Build(
            snapshot, null!, [], 0, ["tables"], null, ReportModel.Dialect, "consistent snapshot", 0, []);

        act.Should().Throw<ArgumentNullException>();
    }

    private static RoleDefinition Role(string name) =>
        new() { Ref = ObjectRef.Role(name), Name = name };

    private static GrantDefinition Grant(string role, string schema, string table) =>
        new()
        {
            Ref = ObjectRef.Grant($"{role}-{schema}.{table}"),
            Name = $"{role}-{schema}.{table}",
            GranteeRef = ObjectRef.Role(role),
            TargetRef = ObjectRef.Table(schema, table),
            Privileges = ["SELECT"],
        };
}
