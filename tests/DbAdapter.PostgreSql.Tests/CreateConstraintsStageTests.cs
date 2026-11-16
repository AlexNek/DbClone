using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.Execution;
using DbAdapter.PostgreSql.Pipeline;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NSubstitute;

using CanonicalModel.Models;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Tests for <see cref="CreateConstraintsStage"/>: only constraints whose parent table is in
/// the copied scope are sent to the server, and a scope consequence is stated once per parent
/// table. The executor is substituted so the suite runs without a live database.
/// </summary>
public class CreateConstraintsStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly IPgConnectionProvider _connectionProvider = Substitute.For<IPgConnectionProvider>();
    private readonly CreateConstraintsStage _stage;

    public CreateConstraintsStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _connectionProvider.GetDestinationConnectionAsync(
                Arg.Any<CopyContext>(),
                Arg.Any<Microsoft.Extensions.Logging.ILogger>(),
                Arg.Any<CancellationToken>())
            .Returns(DestConnection);
        _stage = new CreateConstraintsStage(
            new PgDdlGenerator(),
            _factory,
            _connectionProvider,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ParentInsideScope_CreatesTheConstraint()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var context = CreateContext(
            Table("public", "users"),
            Table("public", "orders", Fk("orders_owner_id_fkey", "public", "users")));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.ObjectsProcessed.Should().Be(1);
        await _executor.Received(1)
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        context.Statistics.ConstraintsCreated.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ParentOutsideScope_IsNeverSentToTheServer()
    {
        // Arrange
        var context = CreateContext(
            Table("public", "comments", Fk("comments_created_by_fkey", "auth", "users")));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
        result.Success.Should().BeTrue("the scope is the user's own choice, not a defect");
        result.ObjectsProcessed.Should().Be(1);
        context.Statistics.ConstraintsCreated.Should().Be(0);
        context.Warnings.Should().ContainSingle()
            .Which.Kind.Should().Be(EStageMessageKind.Skipped);
    }

    [Fact]
    public async Task ExecuteAsync_ManyConstraintsToOneParent_OneSkipLineAndOneWarning()
    {
        // Arrange
        var context = CreateContext(
            Table(
                "public", "orders",
                Fk("orders_created_by_fkey", "auth", "users"),
                Fk("orders_deleted_by_fkey", "auth", "users")),
            Table("public", "comments", Fk("comments_created_by_fkey", "auth", "users")));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        var skip = result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped).Subject;
        skip.ObjectName.Should().Be("auth.users");
        skip.Get<int>(PropKeys.Count).Should().Be(3);
        skip.Get<string>(PropKeys.Reason).Should().Contain("3 foreign key(s) not created");

        context.Warnings.Should().ContainSingle()
            .Which.ObjectName.Should().Be("auth.users");

        result.ObjectsProcessed.Should().Be(3, "deferred constraints are part of the stage's total");
    }

    [Fact]
    public async Task ExecuteAsync_RefusedInScopeConstraint_StaysSuccessfulWithFailedWarning()
    {
        // Arrange — a live parent still refuses when the copied rows violate it.
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("23503: violates foreign key constraint")));
        var context = CreateContext(
            Table("public", "users"),
            Table("public", "orders", Fk("orders_owner_id_fkey", "public", "users")));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("a refused constraint keeps today's behaviour");
        context.Warnings.Should().ContainSingle()
            .Which.Kind.Should().Be(EStageMessageKind.Failed);
        result.Details.Should().NotContain(d => d.Kind == EStageMessageKind.Skipped);
    }

    [Fact]
    public async Task ExecuteAsync_MixedScope_SummaryCountsCreatedFailedAndSkipped()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _executor.ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql != null && sql.Contains("fk_refused")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("23503: violates foreign key constraint")));

        var context = CreateContext(
            Table("public", "users"),
            Table("public", "orders", Fk("fk_ok", "public", "users")),
            Table("public", "comments", Fk("fk_refused", "public", "users")),
            Table("public", "posts", Fk("fk_platform", "auth", "users")));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        var summary = result.Details[0];
        summary.Kind.Should().Be(EStageMessageKind.Summary);
        summary.Get<int>(PropKeys.Total).Should().Be(3);
        summary.Get<int>(PropKeys.Matched).Should().Be(1);
        summary.Get<int>(PropKeys.Failed).Should().Be(1);
        summary.Get<int>(PropKeys.Skipped).Should().Be(1);

        result.ObjectsProcessed.Should().Be(3);
        context.Statistics.ConstraintsCreated.Should().Be(1, "only created constraints are counted");
    }

    [Fact]
    public async Task ExecuteAsync_CopyConstraintsDisabled_SkipsTheWholeStage()
    {
        // Arrange
        var context = CreateContext(
            copyConstraints: false,
            Table("public", "users"),
            Table("public", "orders", Fk("orders_owner_id_fkey", "public", "users")));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle()
            .Which.Kind.Should().Be(EStageMessageKind.Skipped);
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private static ForeignKeyDefinition Fk(string name, string parentSchema, string parentTable) =>
        new(name, ["owner_id"], parentSchema, parentTable, ["id"], "NO ACTION", "NO ACTION", false, false);

    private static TableDefinition Table(
        string schema, string name, params ForeignKeyDefinition[] foreignKeys) =>
        new(schema, name, [], [], foreignKeys, [], [], null, false, null, null);

    private static CopyContext CreateContext(params TableDefinition[] tables) =>
        CreateContext(true, tables);

    private static CopyContext CreateContext(bool copyConstraints, params TableDefinition[] tables)
    {
        var model = new DatabaseModel(
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
        var target = TestTargets.Local();
        var request = new CopyRequest(
            target,
            target,
            model,
            new CopyOptions(CopyConstraints: copyConstraints));
        return new CopyContext
        {
            Request = request,
            SourceModel = model,
            DestinationConnection = new PgCopyConnection(DestConnection, string.Empty)
        };
    }
}
