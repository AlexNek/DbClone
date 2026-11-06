using DbAdapter.Definitions;
using DbClone.Application.Copy;
using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbAdapter.PostgreSql.Execution;
using DbAdapter.PostgreSql.Pipeline;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NSubstitute;
using CanonicalModel.Models;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Tests for <see cref="CopyDataStage"/>: executor is mocked so the suite runs
/// without a live database. Covers early-exit paths and connection failure.
/// Full SkippedTables behavioral tests require PgConnectionHelper to be injectable
/// (future refactoring when adding MS SQL provider).
/// </summary>
public class CopyDataStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly CopyDataStage _stage;

    public CopyDataStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _stage = new CopyDataStage(_factory, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_CopyDataFalse_SkipsEntireStage()
    {
        var context = CreateContext(ECopyMode.Full, copyData: false,
            Table("public", "orders"), Table("public", "items"));

        var result = await _stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionFailed_ReturnsFailureGracefully()
    {
        // Without connection strings, ValidateAndReopenAsync cannot reopen → returns null
        var context = CreateContext(ECopyMode.Full, copyData: true,
            Table("public", "orders"));

        var result = await _stage.ExecuteAsync(context);

        result.Success.Should().BeFalse();
        result.Details.Should().Contain(d => d.Kind == EStageMessageKind.ConnectionFailed);
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection SourceConnection { get; } =
        new("Host=test.example.com;Database=srcdb;Username=test_user;Password=fake_pw_123");

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

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
        var target = TestTargets.Local();
        var request = new CopyRequest(
            target,
            target,
            model,
            new CopyOptions(CopyMode: mode, CopyData: copyData));
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
