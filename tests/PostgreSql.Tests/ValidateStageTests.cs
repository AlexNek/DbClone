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
/// Tests for <see cref="ValidateStage"/>: executor is mocked so the suite runs
/// without a live database. Covers the connection failure early-exit path.
/// Full SkippedTables behavioral tests require PgConnectionHelper to be injectable
/// (future refactoring when adding MS SQL provider).
/// </summary>
public class ValidateStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly ValidateStage _stage;

    public ValidateStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _stage = new ValidateStage(_factory, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionFailed_ReturnsFailureGracefully()
    {
        // Arrange
        // Without valid open connections, PgConnectionHelper returns null → stage reports failure
        var context = CreateContext(Table("public", "orders"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeFalse();
        result.Details.Should().Contain(d => d.Kind == EStageMessageKind.ConnectionFailed);
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection SourceConnection { get; } =
        new("Host=test.example.com;Database=srcdb;Username=test_user;Password=fake_pw_123");

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private static CopyContext CreateContext(params TableDefinition[] tables)
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
            new CopyOptions(CopyData: true));
        return new CopyContext
        {
            Request = request,
            SourceModel = model,
            SourceConnection = SourceConnection,
            DestinationConnection = DestConnection
        };
    }

    private static TableDefinition Table(string schema, string name) =>
        new(schema, name, [], [], [], [], [], null, false, null, null);
}
