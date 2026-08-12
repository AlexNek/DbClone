using DbClone.Application.Copy;
using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.PostgreSql.Execution;
using DbClone.PostgreSql.Pipeline;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NSubstitute;

namespace PostgreSql.Tests;

/// <summary>
/// Tests for <see cref="ReconcileColumnsStage"/>: executor is mocked so the suite runs
/// without a live database. Focuses on SkippedTables filtering and nullability
/// reconciliation logic.
/// </summary>
public class ReconcileColumnsStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly ReconcileColumnsStage _stage;

    public ReconcileColumnsStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _stage = new ReconcileColumnsStage(_factory, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_NothingSkipped_ReconcilesTables()
    {
        // Arrange
        // Table with a NOT NULL column — stage should query destination metadata
        var table = TableWithColumn("public", "orders", "amount", isNullable: false);
        var context = CreateContext(table);

        // Stub the metadata query to return empty (column is nullable on dest)
        StubDestNotNullColumns("public", "orders");

        // Stub the ALTER succeeds
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.ObjectsProcessed.Should().Be(1);
        await _executor.Received(1)
            .ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql.Contains("SET NOT NULL")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_TableInSkippedTables_NotReconciled()
    {
        // Arrange
        var table = TableWithColumn("public", "orders", "amount", isNullable: false);
        var context = CreateContext(table);
        context.SkippedTables.Add(new TableId("public", "orders"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        // No tables left to reconcile — reports as skipped
        result.Details.Should().Contain(d => d.Kind == EStageMessageKind.Skipped);
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _executor.DidNotReceive()
            .QueryAsync(Arg.Any<string>(), Arg.Any<Func<System.Data.Common.DbDataReader, string>>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MixedCaseTableInSkippedTables_MatchesCaseInsensitively()
    {
        // Arrange
        var table = TableWithColumn("Sales", "OrderItems", "price", isNullable: false);
        var context = CreateContext(table);
        // Add with different casing — should still match due to case-insensitive TableId
        context.SkippedTables.Add(new TableId("sales", "orderitems"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Details.Should().Contain(d => d.Kind == EStageMessageKind.Skipped);
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MixOfSkippedAndNormal_OnlyReconcilesNonSkipped()
    {
        // Arrange
        var skippedTable = TableWithColumn("public", "skipped_tbl", "col1", isNullable: false);
        var normalTable = TableWithColumn("public", "normal_tbl", "col2", isNullable: false);
        var context = CreateContext(skippedTable, normalTable);
        context.SkippedTables.Add(new TableId("public", "skipped_tbl"));

        // Stub metadata for normal table — column is nullable on dest
        StubDestNotNullColumns("public", "normal_tbl");
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.ObjectsProcessed.Should().Be(1);
        // Should only query metadata and alter for normal_tbl, not skipped_tbl
        await _executor.Received(1)
            .ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql.Contains("normal_tbl") && sql.Contains("SET NOT NULL")),
                Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ECopyMode.Resume)]
    [InlineData(ECopyMode.Update)]
    public async Task ExecuteAsync_ResumeOrUpdateMode_SkipsEntireStage(ECopyMode mode)
    {
        // Arrange
        var context = CreateContext(mode, true,
            TableWithColumn("public", "orders", "amount", isNullable: false));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    [Fact]
    public async Task ExecuteAsync_CopyDataFalse_SkipsEntireStage()
    {
        // Arrange
        var context = CreateContext(ECopyMode.Full, false,
            TableWithColumn("public", "orders", "amount", isNullable: false));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    [Fact]
    public async Task ExecuteAsync_PartitionChild_NotReconciled()
    {
        // Arrange
        // Partition children (with ParentTable set) should be skipped
        var partChild = new TableDefinition(
            "public", "orders_2024",
            [new ColumnDefinition("amount", "numeric", 1, false, null, false, false, null, null)],
            [], [], [], [], null, false, null, "public.orders");
        var context = CreateContext(partChild);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        // No tables to reconcile — all are partition children
        result.Details.Should().Contain(d => d.Kind == EStageMessageKind.Skipped);
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private static CopyContext CreateContext(params TableDefinition[] tables) =>
        CreateContext(ECopyMode.Full, true, tables);

    private static CopyContext CreateContext(
        ECopyMode mode,
        bool copyData,
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
            new CopyOptions(CopyMode: mode, CopyData: copyData));
        return new CopyContext
        {
            Request = request,
            SourceModel = model,
            DestinationConnection = DestConnection
        };
    }

    private static TableDefinition TableWithColumn(
        string schema, string name, string colName, bool isNullable) =>
        new(schema, name,
            [new ColumnDefinition(colName, "text", 1, isNullable, null, false, false, null, null)],
            [], [], [], [], null, false, null, null);

    /// <summary>
    /// Stubs the destination metadata query to return no NOT NULL columns
    /// (meaning all columns on dest are nullable — reconciliation needed).
    /// </summary>
    private void StubDestNotNullColumns(string schema, string table)
    {
        _executor.QueryAsync(
                Arg.Is<string>(sql => sql.Contains(schema) && sql.Contains(table)),
                Arg.Any<Func<System.Data.Common.DbDataReader, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<string>());
    }
}
