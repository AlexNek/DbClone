using DbClone.Application.Models;
using DbClone.Application.TableFilter;

using FluentAssertions;

namespace Application.Tests;

public class TableFilterApplierTests
{
    private readonly TableFilterApplier _sut = new();

    [Fact]
    public void Apply_NullSpec_ReturnsUnchangedModelAndEmptyReport()
    {
        var model = CreateModel(tables: [Table("public", "orders")]);

        var result = _sut.Apply(model, null);

        result.FilteredModel.Should().BeSameAs(model);
        result.Report.Should().BeSameAs(TableFilterReport.Empty);
    }

    [Fact]
    public void Apply_InactiveSpec_ReturnsUnchangedModel()
    {
        var model = CreateModel(tables: [Table("public", "orders")]);

        var result = _sut.Apply(model, TableSelectionSpec.All);

        result.FilteredModel.Should().BeSameAs(model);
        result.Report.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public void Apply_RemovesExcludedTable()
    {
        var model = CreateModel(
            tables: [Table("public", "orders"), Table("public", "customers")]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        var result = _sut.Apply(model, spec);

        result.FilteredModel.Tables.Should()
            .ContainSingle().Which.Name.Should().Be("customers");
        result.Report.RemovedTables.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "orders"));
    }

    [Fact]
    public void Apply_MatchingIsCaseInsensitive()
    {
        var model = CreateModel(tables: [Table("public", "Orders")]);
        var spec = TableSelectionSpec.Excluding([new TableId("PUBLIC", "orders")]);

        var result = _sut.Apply(model, spec);

        result.FilteredModel.Tables.Should().BeEmpty();
        result.Report.StaleExclusions.Should().BeEmpty();
    }

    [Fact]
    public void Apply_StripsForeignKeysReferencingExcludedTables()
    {
        var danglingFk = ForeignKey("fk_orders_customers", "public", "customers");
        var keptFk = ForeignKey("fk_orders_items", "public", "items");
        var model = CreateModel(
            tables:
            [
                Table("public", "orders", fks: [danglingFk, keptFk]),
                Table("public", "items")
            ]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "customers")]);

        var result = _sut.Apply(model, spec);

        result.FilteredModel.Tables.Single(t => t.Name == "orders").ForeignKeys
            .Should().ContainSingle().Which.Name.Should().Be("fk_orders_items");
        result.Report.DroppedForeignKeys.Should().ContainSingle().Which
            .Should().Be(
                new DroppedForeignKey(
                    new TableId("public", "orders"),
                    "fk_orders_customers",
                    new TableId("public", "customers")));
    }

    [Fact]
    public void Apply_SkipsViewsDependingOnExcludedTables()
    {
        var dependent = new ViewDefinition(
            "public", "order_summary", "SELECT ...", null, ["public.customers"]);
        var independent = new ViewDefinition(
            "public", "item_list", "SELECT ...", null, ["public.items"]);
        var model = CreateModel(
            tables: [Table("public", "items")],
            views: [dependent, independent]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "customers")]);

        var result = _sut.Apply(model, spec);

        result.FilteredModel.Views.Should()
            .ContainSingle().Which.Name.Should().Be("item_list");
        result.Report.SkippedViews.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "order_summary"));
    }

    [Fact]
    public void Apply_RemovesPartitionsOfExcludedParents()
    {
        var partition = Table("public", "orders_2024", parentTable: "public.orders");
        var model = CreateModel(tables: [Table("public", "orders"), partition]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        var result = _sut.Apply(model, spec);

        result.FilteredModel.Tables.Should().BeEmpty();
        result.Report.OrphanedPartitions.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "orders_2024"));
    }

    [Fact]
    public void Apply_RemovesTableOwnedObjects_KeepsStandaloneOnes()
    {
        var ownedSequence = new SequenceDefinition(
            "public", "orders_id_seq", 1, 1, null, null, 1, false, null, null,
            OwnerTable: "public.orders");
        var standaloneSequence = new SequenceDefinition(
            "public", "batch_seq", 1, 1, null, null, 1, false, null, null);
        var trigger = new TriggerDefinition(
            "public", "orders_audit", "orders", "AFTER", ["INSERT"],
            "public", "audit_fn", true, true, null, null);
        var policy = new PolicyDefinition(
            "public", "orders_isolation", "orders", "ALL", true, [], null, null);
        var model = CreateModel(
            tables: [Table("public", "customers")],
            sequences: [ownedSequence, standaloneSequence],
            triggers: [trigger],
            policies: [policy]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        var result = _sut.Apply(model, spec);

        result.FilteredModel.Sequences.Should()
            .ContainSingle().Which.Name.Should().Be("batch_seq");
        result.FilteredModel.Triggers.Should().BeEmpty();
        result.FilteredModel.Policies.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ReportsStaleExclusions()
    {
        var model = CreateModel(tables: [Table("public", "orders")]);
        var spec = TableSelectionSpec.Excluding(
            [new TableId("public", "orders"), new TableId("public", "legacy")]);

        var result = _sut.Apply(model, spec);

        result.Report.StaleExclusions.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "legacy"));
        result.Report.HasWarnings.Should().BeTrue();
    }

    [Theory]
    [InlineData("public.orders", "public", "orders")]
    [InlineData("orders", "", "orders")]
    [InlineData("public.", "", "public.")]
    public void ParseQualified_SplitsAtFirstDot(
        string input,
        string expectedSchema,
        string expectedName)
    {
        var id = _sut.ParseQualified(input);

        id.Schema.Should().Be(expectedSchema);
        id.Name.Should().Be(expectedName);
    }

    [Fact]
    public void Apply_PartitionOfExcludedParent_RemovesOwnedTriggerAndSequence()
    {
        // Arrange
        // Partition "orders_2024" inherits from excluded "orders".
        // Its owned sequence and trigger must be removed too.
        var partition = Table("public", "orders_2024", parentTable: "public.orders");
        var ownedSeq = new SequenceDefinition(
            "public", "orders_2024_id_seq", 1, 1, null, null, 1, false, null, null,
            OwnerTable: "public.orders_2024");
        var trigger = new TriggerDefinition(
            "public", "orders_2024_audit", "orders_2024", "AFTER", ["INSERT"],
            "public", "audit_fn", true, true, null, null);
        var policy = new PolicyDefinition(
            "public", "orders_2024_rls", "orders_2024", "ALL", true, [], null, null);
        var model = CreateModel(
            tables: [Table("public", "orders"), partition, Table("public", "items")],
            sequences: [ownedSeq],
            triggers: [trigger],
            policies: [policy]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should()
            .ContainSingle().Which.Name.Should().Be("items");
        result.FilteredModel.Sequences.Should().BeEmpty();
        result.FilteredModel.Triggers.Should().BeEmpty();
        result.FilteredModel.Policies.Should().BeEmpty();
    }

    [Fact]
    public void Apply_MultiLevelPartition_RemovesGrandchildPartition()
    {
        // Arrange
        // Three levels: orders → orders_2024 → orders_2024_q1
        var parent = Table("public", "orders");
        var child = Table("public", "orders_2024", parentTable: "public.orders");
        var grandchild = Table("public", "orders_2024_q1", parentTable: "public.orders_2024");
        var model = CreateModel(tables: [parent, child, grandchild]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should().BeEmpty();
        result.Report.OrphanedPartitions.Should().HaveCount(2);
        result.Report.OrphanedPartitions.Should().Contain(new TableId("public", "orders_2024"));
        result.Report.OrphanedPartitions.Should().Contain(new TableId("public", "orders_2024_q1"));
    }

    [Fact]
    public void Apply_SkipsMaterializedViewDependingOnExcludedTable()
    {
        // Arrange
        var dependent = new MaterializedViewDefinition(
            "public", "order_stats", "SELECT ...", null, ["count"],
            null, ["public.orders"]);
        var independent = new MaterializedViewDefinition(
            "public", "item_stats", "SELECT ...", null, ["count"],
            null, ["public.items"]);
        var model = CreateModel(
            tables: [Table("public", "items")],
            materializedViews: [dependent, independent]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.MaterializedViews.Should()
            .ContainSingle().Which.Name.Should().Be("item_stats");
        result.Report.SkippedViews.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "order_stats"));
    }

    [Fact]
    public void Apply_ForeignKeyReferencingOrphanedPartition_IsDropped()
    {
        // Arrange
        // FK on "line_items" references "orders_2024" which is a partition of excluded "orders".
        var fk = ForeignKey("fk_lines_orders2024", "public", "orders_2024");
        var partition = Table("public", "orders_2024", parentTable: "public.orders");
        var model = CreateModel(
            tables:
            [
                Table("public", "orders"),
                partition,
                Table("public", "line_items", fks: [fk])
            ]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should()
            .ContainSingle().Which.Name.Should().Be("line_items");
        result.FilteredModel.Tables.Single().ForeignKeys.Should().BeEmpty();
        result.Report.DroppedForeignKeys.Should().ContainSingle().Which
            .ReferencedTable.Should().Be(new TableId("public", "orders_2024"));
    }

    // ── Edge-case / boundary tests ─────────────────────────────────────────────

    [Fact]
    public void Apply_EnabledButEmptyExclusion_ReturnsUnchangedModel()
    {
        // Arrange
        // IsEnabled=true but no tables excluded → IsActive=false → no-op
        var model = CreateModel(tables: [Table("public", "orders")]);
        var spec = new TableSelectionSpec(true, new HashSet<TableId>());

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Should().BeSameAs(model);
        result.Report.Should().BeSameAs(TableFilterReport.Empty);
    }

    [Fact]
    public void Apply_AllTablesExcluded_RemovesEverything()
    {
        // Arrange
        var seq = new SequenceDefinition(
            "public", "orders_id_seq", 1, 1, null, null, 1, false, null, null,
            OwnerTable: "public.orders");
        var view = new ViewDefinition(
            "public", "order_view", "SELECT ...", null, ["public.orders"]);
        var mv = new MaterializedViewDefinition(
            "public", "order_mv", "SELECT ...", null, ["count"], null, ["public.orders"]);
        var trigger = new TriggerDefinition(
            "public", "orders_trg", "orders", "AFTER", ["INSERT"],
            "public", "fn", true, true, null, null);
        var model = CreateModel(
            tables: [Table("public", "orders")],
            views: [view],
            materializedViews: [mv],
            sequences: [seq],
            triggers: [trigger]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should().BeEmpty();
        result.FilteredModel.Views.Should().BeEmpty();
        result.FilteredModel.MaterializedViews.Should().BeEmpty();
        result.FilteredModel.Sequences.Should().BeEmpty();
        result.FilteredModel.Triggers.Should().BeEmpty();
    }

    [Fact]
    public void Apply_SameTableNameDifferentSchemas_OnlyExcludesMatchingSchema()
    {
        // Arrange
        var model = CreateModel(
            tables: [Table("sales", "orders"), Table("archive", "orders")]);
        var spec = TableSelectionSpec.Excluding([new TableId("archive", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should()
            .ContainSingle().Which.SchemaName.Should().Be("sales");
    }

    [Fact]
    public void Apply_MixedCaseNames_CaseInsensitiveExclusion()
    {
        // Arrange
        // User excludes "Public.Orders" but table is stored as "public.orders"
        var model = CreateModel(
            tables: [Table("public", "Orders"), Table("public", "items")]);
        var spec = TableSelectionSpec.Excluding([new TableId("PUBLIC", "ORDERS")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should()
            .ContainSingle().Which.Name.Should().Be("items");
        result.Report.RemovedTables.Should().ContainSingle();
        result.Report.StaleExclusions.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ViewWithNullReferencedRelations_IsKept()
    {
        // Arrange
        var view = new ViewDefinition("public", "simple_view", "SELECT 1", null, null);
        var model = CreateModel(
            tables: [Table("public", "orders")],
            views: [view]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Views.Should()
            .ContainSingle().Which.Name.Should().Be("simple_view");
    }

    [Fact]
    public void Apply_MaterializedViewWithNullReferencedRelations_IsKept()
    {
        // Arrange
        var mv = new MaterializedViewDefinition(
            "public", "simple_mv", "SELECT 1", null, ["x"], null, null);
        var model = CreateModel(
            tables: [Table("public", "orders")],
            materializedViews: [mv]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.MaterializedViews.Should()
            .ContainSingle().Which.Name.Should().Be("simple_mv");
    }

    [Fact]
    public void Apply_ViewDependsOnMultipleTables_SkippedIfAnyExcluded()
    {
        // Arrange
        // View references both "orders" and "items"; only "orders" is excluded
        var view = new ViewDefinition(
            "public", "combined_view", "SELECT ...", null,
            ["public.orders", "public.items"]);
        var model = CreateModel(
            tables: [Table("public", "items")],
            views: [view]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Views.Should().BeEmpty();
        result.Report.SkippedViews.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "combined_view"));
    }

    [Fact]
    public void Apply_AllForeignKeysOnKeptTableDangling_TableKeptWithNoFKs()
    {
        // Arrange
        var fk1 = ForeignKey("fk1", "public", "orders");
        var fk2 = ForeignKey("fk2", "public", "customers");
        var model = CreateModel(
            tables: [Table("public", "line_items", fks: [fk1, fk2])]);
        var spec = TableSelectionSpec.Excluding(
            [new TableId("public", "orders"), new TableId("public", "customers")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should()
            .ContainSingle().Which.ForeignKeys.Should().BeEmpty();
        result.Report.DroppedForeignKeys.Should().HaveCount(2);
    }

    [Fact]
    public void Apply_PartitionExcludedDirectly_AppearsInRemovedNotOrphaned()
    {
        // Arrange
        // User directly excludes the partition (not the parent)
        var partition = Table("public", "orders_2024", parentTable: "public.orders");
        var model = CreateModel(
            tables: [Table("public", "orders"), partition]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders_2024")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should()
            .ContainSingle().Which.Name.Should().Be("orders");
        result.Report.RemovedTables.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "orders_2024"));
        result.Report.OrphanedPartitions.Should().BeEmpty();
    }

    [Fact]
    public void Apply_SequenceOwnedByPartitionOfExcludedParent_IsRemoved()
    {
        // Arrange
        var partition = Table("public", "orders_2024", parentTable: "public.orders");
        var seq = new SequenceDefinition(
            "public", "orders_2024_id_seq", 1, 1, null, null, 1, false, null, null,
            OwnerTable: "public.orders_2024");
        var standaloneSeq = new SequenceDefinition(
            "public", "global_seq", 1, 1, null, null, 1, false, null, null);
        var model = CreateModel(
            tables: [Table("public", "orders"), partition],
            sequences: [seq, standaloneSeq]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Sequences.Should()
            .ContainSingle().Which.Name.Should().Be("global_seq");
    }

    [Fact]
    public void Apply_EmptyModel_NoExclusionsNoCrash()
    {
        // Arrange
        var model = CreateModel();
        var spec = TableSelectionSpec.Excluding([new TableId("public", "ghost")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should().BeEmpty();
        result.Report.RemovedTables.Should().BeEmpty();
        result.Report.StaleExclusions.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "ghost"));
    }

    [Fact]
    public void Apply_PartitionListedBeforeParentInModel_StillOrphaned()
    {
        // Arrange
        // Model has partition first, parent second — iteration must still catch it
        var partition = Table("public", "orders_2024", parentTable: "public.orders");
        var parent = Table("public", "orders");
        var model = CreateModel(tables: [partition, parent]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should().BeEmpty();
        result.Report.OrphanedPartitions.Should()
            .ContainSingle().Which.Should().Be(new TableId("public", "orders_2024"));
    }

    [Fact]
    public void Apply_PartitionWithParentNotExcluded_IsKept()
    {
        // Arrange
        // Parent is not excluded — partition stays
        var partition = Table("public", "orders_2024", parentTable: "public.orders");
        var model = CreateModel(
            tables: [Table("public", "orders"), partition, Table("public", "items")]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "items")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Tables.Should().HaveCount(2);
        result.FilteredModel.Tables.Select(t => t.Name).Should()
            .Contain("orders").And.Contain("orders_2024");
    }

    [Fact]
    public void Apply_MultipleExcludedTablesWithSharedDependents()
    {
        // Arrange
        // View depends on two excluded tables; FK references one excluded table
        var fk = ForeignKey("fk_to_customers", "public", "customers");
        var view = new ViewDefinition(
            "public", "combined", "SELECT ...", null,
            ["public.orders", "public.customers"]);
        var model = CreateModel(
            tables: [Table("public", "items", fks: [fk])],
            views: [view]);
        var spec = TableSelectionSpec.Excluding(
            [new TableId("public", "orders"), new TableId("public", "customers")]);

        // Act
        var result = _sut.Apply(model, spec);

        // Assert
        result.FilteredModel.Views.Should().BeEmpty();
        result.FilteredModel.Tables.Single().ForeignKeys.Should().BeEmpty();
        result.Report.SkippedViews.Should().ContainSingle();
        result.Report.DroppedForeignKeys.Should().ContainSingle();
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static DatabaseModel CreateModel(
        IReadOnlyList<TableDefinition>? tables = null,
        IReadOnlyList<ViewDefinition>? views = null,
        IReadOnlyList<MaterializedViewDefinition>? materializedViews = null,
        IReadOnlyList<SequenceDefinition>? sequences = null,
        IReadOnlyList<TriggerDefinition>? triggers = null,
        IReadOnlyList<PolicyDefinition>? policies = null) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
            Schemas: [],
            Tables: tables ?? [],
            Views: views ?? [],
            MaterializedViews: materializedViews ?? [],
            Sequences: sequences ?? [],
            Enums: [],
            Domains: [],
            CompositeTypes: [],
            Functions: [],
            Triggers: triggers ?? [],
            Policies: policies ?? [],
            Publications: [],
            Subscriptions: [],
            Extensions: []);

    private static TableDefinition Table(
        string schema,
        string name,
        IReadOnlyList<ForeignKeyDefinition>? fks = null,
        string? parentTable = null) =>
        new(schema, name, [], [], fks ?? [], [], [], null, false, null, parentTable);

    private static ForeignKeyDefinition ForeignKey(
        string name,
        string referencedSchema,
        string referencedTable) =>
        new(name, ["fk_col"], referencedSchema, referencedTable, ["id"],
            "NO ACTION", "NO ACTION", false, false);
}
