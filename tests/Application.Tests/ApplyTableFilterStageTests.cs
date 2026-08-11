using DbClone.Application.Copy;
using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Models;
using DbClone.Application.TableFilter;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Tests;

public class ApplyTableFilterStageTests
{
    [Fact]
    public async Task ExecuteAsync_NoActiveSpec_SucceedsWithoutTouchingModel()
    {
        var model = CreateModel(Table("public", "orders"));
        var context = CreateContext(null, model);
        var stage = CreateStage();

        var result = await stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        context.SourceModel.Should().BeSameAs(model);
    }

    [Fact]
    public async Task ExecuteAsync_ActiveSpec_FiltersSourceModel()
    {
        var model = CreateModel(Table("public", "orders"), Table("public", "customers"));
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);
        var context = CreateContext(spec, model);
        var stage = CreateStage();

        var result = await stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        result.ObjectsProcessed.Should().Be(1);
        context.SourceModel!.Tables.Should()
            .ContainSingle().Which.Name.Should().Be("customers");
    }

    [Fact]
    public async Task ExecuteAsync_AllTablesExcluded_FailsWithError()
    {
        var model = CreateModel(Table("public", "orders"), Table("public", "customers"));
        var spec = TableSelectionSpec.Excluding(
            [new TableId("public", "orders"), new TableId("public", "customers")]);
        var context = CreateContext(spec, model);
        var stage = CreateStage();

        var result = await stage.ExecuteAsync(context);

        result.Success.Should().BeFalse();
        context.Errors.Should().ContainSingle()
            .Which.StageName.Should().Be(ECopyStage.ApplyTableFilter);
    }

    [Fact]
    public async Task ExecuteAsync_EmptySourceDatabase_DoesNotFail()
    {
        // A database without tables must not fail solely because the selection
        // resolves to zero tables.
        var model = CreateModel();
        var spec = TableSelectionSpec.Excluding([new TableId("public", "ghost")]);
        var context = CreateContext(spec, model);
        var stage = CreateStage();

        var result = await stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        context.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_EmitsSkippedWarningsForDependencyAdjustments()
    {
        var fk = new ForeignKeyDefinition(
            "fk_orders_customers", ["customer_id"], "public", "customers", ["id"],
            "NO ACTION", "NO ACTION", false, false);
        var model = CreateModel(
            tables:
            [
                Table("public", "orders", fks: [fk]),
                Table("public", "customers"),
                Table("public", "customers_2024", parentTable: "public.customers")
            ],
            views:
            [
                new ViewDefinition(
                    "public", "customer_view", "SELECT ...", null, ["public.customers"])
            ]);
        // "ghost" no longer exists → stale exclusion warning
        var spec = TableSelectionSpec.Excluding(
            [new TableId("public", "customers"), new TableId("public", "ghost")]);
        var context = CreateContext(spec, model);
        var stage = CreateStage();

        var result = await stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        context.Warnings.Should().HaveCount(4); // stale + FK + view + partition
        context.Warnings.Should().OnlyContain(
            w => w.StageName == ECopyStage.ApplyTableFilter
                 && w.Kind == EStageMessageKind.Skipped);
    }

    [Fact]
    public async Task Pipeline_AbortsAfterFailedFilterStage()
    {
        // ApplyTableFilter is critical: a failure must never continue unfiltered.
        var model = CreateModel(Table("public", "orders"));
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);
        var context = CreateContext(spec, model);
        var pipeline = new CopyPipeline(
            [CreateStage(), new SuccessStage(ECopyStage.CreateTables, 40)],
            NullLogger<CopyPipeline>.Instance);

        var result = await pipeline.ExecuteAsync(context);

        result.Success.Should().BeFalse();
        result.StageResults.Should().ContainSingle(); // the next stage never ran
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static ApplyTableFilterStage CreateStage() =>
        new(NullLogger<ApplyTableFilterStage>.Instance, new TableFilterApplier());

    private static CopyContext CreateContext(TableSelectionSpec? spec, DatabaseModel model)
    {
        var request = new CopyRequest(
            new ConnectionInfo("test.example.com", 5432, "testdb", "test_user", "pass", ESslMode.Prefer),
            new ConnectionInfo("test.example.com", 5432, "testdb", "test_user", "pass", ESslMode.Prefer),
            new CopyOptions(TableSelection: spec));
        return new CopyContext { Request = request, SourceModel = model };
    }

    private static DatabaseModel CreateModel(params TableDefinition[] tables) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
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

    private static DatabaseModel CreateModel(
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyList<ViewDefinition> views) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
            Schemas: [],
            Tables: tables,
            Views: views,
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

    private static TableDefinition Table(
        string schema,
        string name,
        IReadOnlyList<ForeignKeyDefinition>? fks = null,
        string? parentTable = null) =>
        new(schema, name, [], [], fks ?? [], [], [], null, false, null, parentTable);

    private sealed class SuccessStage(ECopyStage name, int order) : ICopyStage
    {
        public ECopyStage Name { get; } = name;

        public int Order { get; } = order;

        public Task<StageResult> ExecuteAsync(
            CopyContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageResult(Name, true, TimeSpan.Zero, 0, []));
    }
}
