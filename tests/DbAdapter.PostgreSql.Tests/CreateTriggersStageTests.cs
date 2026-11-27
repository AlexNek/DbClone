using DbAdapter.Definitions;
using DbClone.Application.Copy;
using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.Execution;
using DbAdapter.PostgreSql.Pipeline;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CanonicalModel.Models;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Hermetic tests for <see cref="CreateTriggersStage"/>: the executor is substituted and the
/// destination connection is constructed but never opened, so the suite runs without a live
/// database. A refused trigger is one object lost — one warning naming it and a stage that
/// still succeeds — never a failed run.
/// </summary>
public class CreateTriggersStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly IPgConnectionProvider _connectionProvider = Substitute.For<IPgConnectionProvider>();
    private readonly CreateTriggersStage _stage;

    public CreateTriggersStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _connectionProvider.GetDestinationConnectionAsync(
                Arg.Any<CopyContext>(),
                Arg.Any<Microsoft.Extensions.Logging.ILogger>(),
                Arg.Any<CancellationToken>())
            .Returns(DestConnection);
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _stage = new CreateTriggersStage(
            new PgDdlGenerator(),
            _factory,
            _connectionProvider,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_EveryTriggerCreated_ReportsCreatedOverTotal()
    {
        // Arrange
        var context = CreateContext(
            MakeTrigger("public", "trg_audit", "public", "orders"),
            MakeTrigger("public", "trg_touch", "public", "orders"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        context.Warnings.Should().BeEmpty();
        context.Errors.Should().BeEmpty();
        context.Statistics.TriggersCopied.Should().Be(2);

        var summary = result.Details.Single(d => d.Kind == EStageMessageKind.Summary);
        summary.Get<int>(PropKeys.Total).Should().Be(2);
        summary.Get<int>(PropKeys.Matched).Should().Be(2);
        summary.Get<int>(PropKeys.Failed).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_TriggerRefused_ReportsOneWarningAndSucceeds()
    {
        // Arrange
        _executor
            .ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql.Contains("broken_trigger", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("function audit_fn() does not exist"));
        var context = CreateContext(
            MakeTrigger("public", "trg_audit", "public", "orders"),
            MakeTrigger("public", "broken_trigger", "public", "orders"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("a refused trigger is one object lost, not a failed run");
        context.Errors.Should().BeEmpty();
        context.Warnings.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                StageName = ECopyStage.CreateTriggers,
                Kind = EStageMessageKind.Failed,
                ObjectName = "public.broken_trigger"
            },
            opts => opts.ExcludingMissingMembers());
        context.Statistics.TriggersCopied.Should().Be(1);
        result.Details.Should()
                .ContainSingle(
                    d => d.Kind == EStageMessageKind.Failed
                        && d.Level == ELogLevel.Warning
                        && d.ObjectName == "public.broken_trigger"
                        && d.Get<string>(PropKeys.Reason) == "function audit_fn() does not exist");
        result.Details.Should().NotContain(d => d.Level == ELogLevel.Error);

        var summary = result.Details.Single(d => d.Kind == EStageMessageKind.Summary);
        summary.Get<int>(PropKeys.Total).Should().Be(2);
        summary.Get<int>(PropKeys.Matched).Should().Be(1);
        summary.Get<int>(PropKeys.Failed).Should().Be(1);
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private static CopyContext CreateContext(params TriggerDefinition[] triggers)
    {
        var model = new DatabaseModel(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
            Schemas: [],
            Tables: [],
            Views: [],
            MaterializedViews: [],
            Sequences: [],
            Enums: [],
            Domains: [],
            CompositeTypes: [],
            Functions: [],
            Triggers: triggers,
            Policies: [],
            Publications: [],
            Subscriptions: [],
            Extensions: []);
        var target = TestTargets.Local();
        var request = new CopyRequest(target, target, model, new CopyOptions());
        return new CopyContext
        {
            Request = request,
            SourceModel = model,
            DestinationConnection = new PgCopyConnection(DestConnection, string.Empty)
        };
    }

    private static TriggerDefinition MakeTrigger(
        string schema,
        string name,
        string functionSchema,
        string functionName) =>
        new(schema,
            name,
            "orders",
            "BEFORE",
            ["INSERT"],
            functionSchema,
            functionName,
            true,
            true,
            null,
            null);
}
