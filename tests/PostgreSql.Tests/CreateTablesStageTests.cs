using DbClone.Application.Copy;
using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;
using DbClone.PostgreSql.Pipeline;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace PostgreSql.Tests;

/// <summary>
/// Tests for <see cref="CreateTablesStage"/>: executor is mocked so the suite runs
/// without a live database. Focuses on the SkippedTables population and
/// extension-blocking logic.
/// </summary>
public class CreateTablesStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly CreateTablesStage _stage;

    public CreateTablesStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _stage = new CreateTablesStage(new PgDdlGenerator(), _factory, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulCreation_DoesNotPopulateSkippedTables()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var context = CreateContext(Table("public", "orders"), Table("public", "items"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.ObjectsProcessed.Should().Be(2);
        context.SkippedTables.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_FailedTable_AddsToSkippedTablesAsTableId()
    {
        // Arrange
        // First table succeeds, second fails
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                x => 0,
                x => throw new InvalidOperationException("type not supported"));
        var context = CreateContext(Table("public", "orders"), Table("public", "bad_table"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeFalse();
        context.SkippedTables.Should().ContainSingle()
            .Which.Should().Be(new TableId("public", "bad_table"));
    }

    [Fact]
    public async Task ExecuteAsync_MixedCaseTable_SkippedTablesUsesOriginalCasing()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("fail"));
        var context = CreateContext(Table("Sales", "OrderItems"));

        // Act
        await _stage.ExecuteAsync(context);

        // Assert
        // Lookup with different casing must match (TableId is case-insensitive)
        context.SkippedTables.Contains(new TableId("sales", "orderitems"))
            .Should().BeTrue();
        context.SkippedTables.Contains(new TableId("SALES", "ORDERITEMS"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ExtensionBlockedTable_SkippedAndReportedAsWarning()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("type vault.secret not found"));
        var context = CreateContext(Table("vault", "secrets"));
        context.SkippedExtensions["supabase_vault"] = "vault";

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("extension-blocked tables don't fail the stage");
        context.SkippedTables.Should().ContainSingle()
            .Which.Should().Be(new TableId("vault", "secrets"));
        context.Warnings.Should().ContainSingle()
            .Which.Properties.Should().ContainKey(PropKeys.Extension);
        context.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ExtensionBlockedMixedCase_MatchesCaseInsensitively()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("type not found"));
        var context = CreateContext(Table("Vault", "Secrets"));
        context.SkippedExtensions["supabase_vault"] = "vault";

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        context.SkippedTables.Should().ContainSingle()
            .Which.Should().Be(new TableId("vault", "secrets"));
    }

    [Fact]
    public async Task ExecuteAsync_MultipleFailures_AllAddedToSkippedTables()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("fail"));
        var context = CreateContext(
            Table("public", "t1"), Table("public", "t2"), Table("public", "t3"));

        // Act
        await _stage.ExecuteAsync(context);

        // Assert
        context.SkippedTables.Should().HaveCount(3);
        context.SkippedTables.Should().Contain(new TableId("public", "t1"));
        context.SkippedTables.Should().Contain(new TableId("public", "t2"));
        context.SkippedTables.Should().Contain(new TableId("public", "t3"));
    }

    [Fact]
    public async Task ExecuteAsync_ResumeMode_SkipsEntireStage()
    {
        // Arrange
        var context = CreateContext([Table("public", "orders")], ECopyMode.Resume);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private static CopyContext CreateContext(params TableDefinition[] tables) =>
        CreateContext(tables, ECopyMode.Full);

    private static CopyContext CreateContext(TableDefinition[] tables, ECopyMode mode)
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
        var request = new CopyRequest(
            new ConnectionInfo("test.example.com", 5432, "testdb", "test_user", "pass", ESslMode.Prefer),
            new ConnectionInfo("test.example.com", 5432, "testdb", "test_user", "pass", ESslMode.Prefer),
            new CopyOptions(CopyMode: mode));
        return new CopyContext
        {
            Request = request,
            SourceModel = model,
            DestinationConnection = DestConnection
        };
    }

    private static TableDefinition Table(string schema, string name) =>
        new(schema, name, [], [], [], [], [], null, false, null, null);
}
