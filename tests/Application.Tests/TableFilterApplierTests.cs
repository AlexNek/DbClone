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

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static DatabaseModel CreateModel(
        IReadOnlyList<TableDefinition>? tables = null,
        IReadOnlyList<ViewDefinition>? views = null,
        IReadOnlyList<SequenceDefinition>? sequences = null,
        IReadOnlyList<TriggerDefinition>? triggers = null,
        IReadOnlyList<PolicyDefinition>? policies = null) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
            Schemas: [],
            Tables: tables ?? [],
            Views: views ?? [],
            MaterializedViews: [],
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
