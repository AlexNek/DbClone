using DbAdapter.Definitions;
using DbClone.Application.Copy;
using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using CanonicalModel.Models;

namespace Application.Tests;

/// <summary>
/// The table-selection policy the copy workflow owns — the behaviour that used to live
/// in the ApplyTableFilter pipeline stage.
/// </summary>
public class CopyWorkflowTests
{
    [Fact]
    public void Prepare_NoActiveSelection_BuildsRequestAndReportsInactiveRow()
    {
        // Arrange
        var model = CreateModel(Table("public", "orders"));
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), tableSelection: null);

        // Assert
        step.Request.Should().NotBeNull();
        step.Request!.Model.Should().BeSameAs(model);
        step.Warnings.Should().BeEmpty();
        step.FailedResult.Should().BeNull();
        step.Stage.StageName.Should().Be(ECopyStage.ApplyTableFilter);
        step.Stage.Success.Should().BeTrue();
        step.Stage.ObjectsProcessed.Should().Be(0);
        step.Stage.Details.Should().ContainSingle().Which
            .Get<string>(PropKeys.Reason).Should().Be("No table selection active");
    }

    [Fact]
    public void Prepare_ActiveSelection_FiltersTheRequestModel()
    {
        // Arrange
        var model = CreateModel(Table("public", "orders"), Table("public", "customers"));
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), spec);

        // Assert
        step.Stage.Success.Should().BeTrue();
        step.Stage.ObjectsProcessed.Should().Be(1);
        step.Request!.Model.Tables.Should()
            .ContainSingle().Which.Name.Should().Be("customers");
        step.Stage.Details.Should().HaveCount(2);
        step.Stage.Details.Should().OnlyContain(
            d => d.Kind == EStageMessageKind.Statistic);
    }

    [Fact]
    public void Prepare_AllTablesExcluded_FailsBeforeAnyRequestIsBuilt()
    {
        // Arrange
        var model = CreateModel(Table("public", "orders"), Table("public", "customers"));
        var spec = TableSelectionSpec.Excluding(
                       [new TableId("public", "orders"), new TableId("public", "customers")]);
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), spec);

        // Assert
        step.Request.Should().BeNull();
        step.FailedResult.Should().NotBeNull();
        step.FailedResult!.Success.Should().BeFalse();
        step.FailedResult.Errors.Should().ContainSingle()
            .Which.StageName.Should().Be(ECopyStage.ApplyTableFilter);
        step.Stage.Success.Should().BeFalse();
        step.Stage.Details.Should().ContainSingle().Which
            .ObjectName.Should().Be("Table selection");
    }

    [Fact]
    public void Prepare_EmptySourceDatabase_DoesNotFail()
    {
        // A database without tables must not fail solely because the selection
        // resolves to zero tables.
        // Arrange
        var model = CreateModel();
        var spec = TableSelectionSpec.Excluding([new TableId("public", "ghost")]);
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), spec);

        // Assert
        step.Stage.Success.Should().BeTrue();
        step.FailedResult.Should().BeNull();
        step.Request.Should().NotBeNull();
    }

    [Fact]
    public void Prepare_SelectionAdjustingDependencies_RaisesFourWarnings()
    {
        // Arrange
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
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), spec);

        // Assert
        step.Stage.Success.Should().BeTrue();
        step.Warnings.Should().HaveCount(4); // stale + FK + view + partition
        step.Warnings.Should().OnlyContain(
            w => w.StageName == ECopyStage.ApplyTableFilter
                 && w.Kind == EStageMessageKind.Skipped);
    }

    [Fact]
    public void Prepare_MixedCaseExclusion_FiltersCorrectly()
    {
        // Arrange
        var model = CreateModel(Table("Sales", "Orders"), Table("public", "items"));
        var spec = TableSelectionSpec.Excluding([new TableId("SALES", "orders")]);
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), spec);

        // Assert
        step.Stage.Success.Should().BeTrue();
        step.Request!.Model.Tables.Should()
            .ContainSingle().Which.Name.Should().Be("items");
    }

    [Fact]
    public void Prepare_PartitionWithTrigger_WarnsAndRemovesTrigger()
    {
        // Arrange
        var partition = Table("public", "orders_2024", parentTable: "public.orders");
        var trigger = new TriggerDefinition(
            "public", "orders_2024_audit", "orders_2024", "AFTER", ["INSERT"],
            "public", "audit_fn", true, true, null, null);
        var model = CreateModel(
            tables: [Table("public", "orders"), partition, Table("public", "items")],
            triggers: [trigger]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), spec);

        // Assert
        step.Stage.Success.Should().BeTrue();
        step.Request!.Model.Triggers.Should().BeEmpty();
        step.Warnings.Should().Contain(w => w.ObjectName == "public.orders_2024");
    }

    [Fact]
    public void Prepare_MaterializedViewOnExcludedTable_WarnsAndRemovesView()
    {
        // Arrange
        var mv = new MaterializedViewDefinition(
            "public", "order_mv", "SELECT ...", null, ["count"], null, ["public.orders"]);
        var model = CreateModel(
            tables: [Table("public", "items")],
            materializedViews: [mv]);
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), spec);

        // Assert
        step.Stage.Success.Should().BeTrue();
        step.Request!.Model.MaterializedViews.Should().BeEmpty();
        step.Warnings.Should().Contain(w => w.ObjectName == "public.order_mv");
    }

    [Fact]
    public void Prepare_BuildsBothTargetsFromTheConnections()
    {
        // Arrange
        var model = CreateModel(Table("public", "orders"));
        var workflow = CreateWorkflow(model);

        // Act
        var step = workflow.PrepareCopyRequest(
            model, Source(), Destination(), new CopyOptions(), tableSelection: null);

        // Assert
        step.Request!.Source.Dialect.Should().Be("postgresql");
        step.Request.Source.ConnectionString.Should().Contain("Database=source_db");
        step.Request.Target.Dialect.Should().Be("postgresql");
        step.Request.Target.ConnectionString.Should().Contain("Database=dest_db");
    }

    [Fact]
    public async Task Execute_SameDatabase_IsRefusedBeforeAnythingIsTouched()
    {
        // A full copy cleans the destination before it reads the source, so a copy onto
        // the source itself must be refused before a connection is opened.
        // Arrange
        var workflow = CreateWorkflow(CreateModel(Table("public", "orders")));
        var listener = Substitute.For<ICopyProgressSink>();
        var same = Source();

        // Act
        var result = await workflow.ExecuteAsync(
            same, same, backupNamePrefix: null, new CopyOptions(), tableSelection: null,
            listener, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Source and destination are the same database");
        listener.Received(1).OnPhaseChanged(ECopyOperationPhase.Failed);
        listener.DidNotReceive().OnOperationComplete();
    }

    [Fact]
    public async Task Execute_SameDatabaseIgnoringCaseAndSpaces_IsRefused()
    {
        // Arrange
        var workflow = CreateWorkflow(CreateModel(Table("public", "orders")));
        var listener = Substitute.For<ICopyProgressSink>();
        var source = new ConnectionInfo("TEST.example.com ", 5432, "Source_DB", "test_user", "pass", "Prefer");
        var destination = new ConnectionInfo(" test.example.com", 5432, "source_db", "test_user", "pass", "Prefer");

        // Act
        var result = await workflow.ExecuteAsync(
            source, destination, null, new CopyOptions(), null, listener, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Source and destination are the same database");
    }

    [Fact]
    public async Task Execute_DifferentDatabases_PassesTheIdentityGuard()
    {
        // The substituted adapter cannot connect, so the run stops at the source connection
        // check — far enough to prove the identity guard let the pair through.
        // Arrange
        var admin = Substitute.For<IDatabaseAdmin>();
        admin.TestConnectionAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var adapter = Substitute.For<IDatabaseAdapter>();
        adapter.CreateAdmin(Arg.Any<DatabaseTarget>()).Returns(admin);

        var workflow = CreateWorkflow(CreateModel(Table("public", "orders")), adapter);
        var listener = Substitute.For<ICopyProgressSink>();

        // Act
        var result = await workflow.ExecuteAsync(
            Source(), Destination(), null, new CopyOptions(), null, listener, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Cannot connect to source");
    }

    [Fact]
    public async Task Execute_ServerVersionOutsideAdapterRange_IsRefused()
    {
        // The adapter says which servers it can work with; a server outside that range must be
        // refused before the copy touches the destination, not after it has started writing.
        // Arrange
        var admin = Substitute.For<IDatabaseAdmin>();
        admin.TestConnectionAsync(Arg.Any<CancellationToken>()).Returns("17.2");

        var adapter = Substitute.For<IDatabaseAdapter>();
        adapter.CreateAdmin(Arg.Any<DatabaseTarget>()).Returns(admin);

        var workflow = CreateWorkflow(
            CreateModel(Table("public", "orders")), adapter, ">=15.0 <17.0");
        var listener = Substitute.For<ICopyProgressSink>();

        // Act
        var result = await workflow.ExecuteAsync(
            Source(), Destination(), null, new CopyOptions(), null, listener, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("outside the range this adapter supports");
        result.ErrorMessage.Should().Contain(">=15.0 <17.0");
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static CopyWorkflow CreateWorkflow(
        DatabaseModel model,
        IDatabaseAdapter? adapter = null,
        string engineVersionRange = "*")
    {
        var filterApplier = new TableFilterApplier();
        var targetFactory = CreateTargetFactory(adapter, engineVersionRange);
        var confirmationService = Substitute.For<IConfirmationService>();

        return new CopyWorkflow(
            targetFactory,
            filterApplier,
            confirmationService,
            NullLogger<CopyWorkflow>.Instance);
    }

    private static DatabaseTargetFactory CreateTargetFactory(
        IDatabaseAdapter? adapter = null,
        string engineVersionRange = "*")
    {
        var syntax = Substitute.For<IConnectionStringSyntax>();
        syntax.BuildKeyValue(Arg.Any<ConnectionStringFields>())
            .Returns(call => "Host=test.example.com;Port=5432;Database="
                             + call.Arg<ConnectionStringFields>()?.Database);

        adapter ??= Substitute.For<IDatabaseAdapter>();
        adapter.Info.Returns(
            new AdapterInfo("test.postgresql", "postgresql", engineVersionRange, "1.0.0"));
        adapter.Syntax.Returns(syntax);

        var catalog = Substitute.For<DbClone.Application.Adapters.IAdapterCatalog>();
        catalog.All.Returns(new[] { adapter });
        catalog.Find(Arg.Any<string>()).Returns(adapter);

        return new DatabaseTargetFactory(catalog);
    }

    private static ConnectionInfo Source() =>
        new("test.example.com", 5432, "source_db", "test_user", "pass", "Prefer");

    private static ConnectionInfo Destination() =>
        new("test.example.com", 5432, "dest_db", "test_user", "pass", "Prefer");

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
        IReadOnlyList<TableDefinition>? tables = null,
        IReadOnlyList<ViewDefinition>? views = null,
        IReadOnlyList<MaterializedViewDefinition>? materializedViews = null,
        IReadOnlyList<TriggerDefinition>? triggers = null) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
            Schemas: [],
            Tables: tables ?? [],
            Views: views ?? [],
            MaterializedViews: materializedViews ?? [],
            Sequences: [],
            Enums: [],
            Domains: [],
            CompositeTypes: [],
            Functions: [],
            Triggers: triggers ?? [],
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

    private static DatabaseModel WithSchemas(params SchemaDefinition[] schemas) =>
        CreateModel(Table("public", "orders")) with { Schemas = schemas };

    private static SchemaDefinition Schema(string name) => new(name, "test_user");
}
