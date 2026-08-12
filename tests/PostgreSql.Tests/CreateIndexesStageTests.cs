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

namespace PostgreSql.Tests;

/// <summary>
/// Tests for <see cref="CreateIndexesStage"/>: executor is mocked so the suite runs
/// without a live database. Focuses on SkippedTables filtering and early-exit paths.
/// </summary>
public class CreateIndexesStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly CreateIndexesStage _stage;

    public CreateIndexesStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _stage = new CreateIndexesStage(new PgDdlGenerator(), _factory, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_NothingSkipped_CreatesAllIndexes()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var table = TableWithIndexes("public", "orders",
            new IndexDefinition("idx_orders_date", ["order_date"], false, false, null, null));
        var context = CreateContext(table);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.ObjectsProcessed.Should().Be(1);
        await _executor.Received(1)
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_TableInSkippedTables_IndexesAreSkipped()
    {
        // Arrange
        var table = TableWithIndexes("public", "orders",
            new IndexDefinition("idx_orders_date", ["order_date"], false, false, null, null),
            new IndexDefinition("idx_orders_customer", ["customer_id"], false, false, null, null));
        var context = CreateContext(table);
        context.SkippedTables.Add(new TableId("public", "orders"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        // Indexes for skipped tables are reported as skipped, not created
        result.Success.Should().BeFalse("skipped indexes count as non-success");
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MixedCaseTableInSkippedTables_MatchesCaseInsensitively()
    {
        // Arrange
        var table = TableWithIndexes("Sales", "OrderItems",
            new IndexDefinition("idx_oi_product", ["product_id"], false, false, null, null));
        var context = CreateContext(table);
        // Add with different casing — should still match
        context.SkippedTables.Add(new TableId("sales", "orderitems"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MixOfSkippedAndNormal_OnlyCreatesNonSkippedIndexes()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var skippedTable = TableWithIndexes("public", "skipped_tbl",
            new IndexDefinition("idx_skip", ["col1"], false, false, null, null));
        var normalTable = TableWithIndexes("public", "normal_tbl",
            new IndexDefinition("idx_normal", ["col2"], false, false, null, null));
        var context = CreateContext(skippedTable, normalTable);
        context.SkippedTables.Add(new TableId("public", "skipped_tbl"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        // Only the normal table's index should have been created
        await _executor.Received(1)
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ECopyMode.Resume)]
    [InlineData(ECopyMode.Update)]
    public async Task ExecuteAsync_ResumeOrUpdateMode_SkipsEntireStage(ECopyMode mode)
    {
        // Arrange
        var context = CreateContext(mode, true,
            TableWithIndexes("public", "orders",
                new IndexDefinition("idx_test", ["col1"], false, false, null, null)));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    [Fact]
    public async Task ExecuteAsync_CopyIndexesFalse_SkipsWithMessage()
    {
        // Arrange
        var context = CreateContext(ECopyMode.Full, false,
            TableWithIndexes("public", "orders",
                new IndexDefinition("idx_test", ["col1"], false, false, null, null)));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    [Fact]
    public async Task ExecuteAsync_PrimaryKeyIndex_NotCreated()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var table = TableWithIndexes("public", "orders",
            new IndexDefinition("pk_orders", ["id"], false, true, null, null));
        var context = CreateContext(table);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        // Primary keys are created with CREATE TABLE, not here
        result.ObjectsProcessed.Should().Be(0);
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private static CopyContext CreateContext(params TableDefinition[] tables) =>
        CreateContext(ECopyMode.Full, true, tables);

    private static CopyContext CreateContext(
        ECopyMode mode,
        bool copyIndexes,
        params TableDefinition[] tables)
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
            new ConnectionInfo("test.example.com", 5432, "testdb", "test_user", "fake_pw_123", ESslMode.Prefer),
            new ConnectionInfo("test.example.com", 5432, "testdb", "test_user", "fake_pw_123", ESslMode.Prefer),
            new CopyOptions(CopyMode: mode, CopyIndexes: copyIndexes));
        return new CopyContext
        {
            Request = request,
            SourceModel = model,
            DestinationConnection = DestConnection
        };
    }

    private static TableDefinition TableWithIndexes(
        string schema, string name, params IndexDefinition[] indexes) =>
        new(schema, name, [], indexes, [], [], [], null, false, null, null);
}
