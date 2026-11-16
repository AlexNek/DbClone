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
/// Hermetic tests for <see cref="RetryFunctionsStage"/>: the executor is substituted and the
/// destination connection is constructed but never opened, so the suite runs without a live
/// database. A function the server refuses is one object lost — one warning naming it and a
/// stage that still succeeds — never a failed run.
/// </summary>
public class RetryFunctionsStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly IPgConnectionProvider _connectionProvider = Substitute.For<IPgConnectionProvider>();
    private readonly RetryFunctionsStage _stage;

    public RetryFunctionsStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _connectionProvider.GetDestinationConnectionAsync(
                Arg.Any<CopyContext>(),
                Arg.Any<Microsoft.Extensions.Logging.ILogger>(),
                Arg.Any<CancellationToken>())
            .Returns(DestConnection);
        _stage = new RetryFunctionsStage(
            new PgDdlGenerator(),
            _factory,
            _connectionProvider,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_RetriedFunctionStillFails_ReportsOneWarningAndSucceeds()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("permission denied for function list_changes"));
        var context = CreateContext(MakeFunction("realtime", "list_changes"));
        MarkFunctionsFailed(context, 0);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("a refused function is one object lost, not a failed run");
        context.Errors.Should().BeEmpty();
        context.Warnings.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                StageName = ECopyStage.RetryFunctions,
                Kind = EStageMessageKind.Failed,
                ObjectName = "realtime.list_changes"
            },
            opts => opts.ExcludingMissingMembers());
        result.Details.Should()
                .ContainSingle(
                    d => d.Kind == EStageMessageKind.Failed
                        && d.Level == ELogLevel.Warning
                        && d.ObjectName == "realtime.list_changes"
                        && d.Get<string>(PropKeys.Reason) == "permission denied for function list_changes");
        result.Details.Should().NotContain(d => d.Level == ELogLevel.Error);
        context.Statistics.FunctionsCopied.Should().Be(0);

        var summary = result.Details.Single(d => d.Kind == EStageMessageKind.Summary);
        summary.Get<int>(PropKeys.Total).Should().Be(1);
        summary.Get<int>(PropKeys.Matched).Should().Be(0);
        summary.Get<int>(PropKeys.Failed).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RefusedParameterWasNotGranted_StatesTheStatementInTheWarning()
    {
        // Arrange — the pre-flight could not win the privilege, so the loss is one this run knows
        // the statement for; the warning the verdict counts must carry it.
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(
                new PostgresException(
                    "permission denied to set parameter \"log_min_messages\"",
                    "ERROR",
                    "ERROR",
                    "42501"));
        var context = CreateContext(MakeFunction("realtime", "list_changes", "log_min_messages"));
        MarkFunctionsFailed(context, 0);
        context.Properties[PgSetParameterGrant.RefusedSetParametersKey] =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["log_min_messages"] = "GRANT SET ON PARAMETER log_min_messages TO test_user"
            };

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        var reason = result.Details.Single(d => d.Kind == EStageMessageKind.Failed)
                           .Get<string>(PropKeys.Reason);
        reason.Should()
              .Be(
                  "permission denied to set parameter \"log_min_messages\" — run on the destination: " +
                  "GRANT SET ON PARAMETER log_min_messages TO test_user");
        context.Warnings.Should().ContainSingle()
               .Which.Properties![PropKeys.Reason].Should().Be(reason);
    }

    [Fact]
    public async Task ExecuteAsync_RetriedFunctionCreated_ReportsNoWarning()
    {
        // Arrange
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var context = CreateContext(MakeFunction("public", "list_changes"));
        MarkFunctionsFailed(context, 0);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        context.Warnings.Should().BeEmpty();
        context.Statistics.FunctionsCopied.Should().Be(1);
        result.Details.Should().Contain(
            d => d.Kind == EStageMessageKind.Created && d.ObjectName == "public.list_changes");

        var summary = result.Details.Single(d => d.Kind == EStageMessageKind.Summary);
        summary.Get<int>(PropKeys.Total).Should().Be(1);
        summary.Get<int>(PropKeys.Matched).Should().Be(1);
        summary.Get<int>(PropKeys.Failed).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoFunctionsToRetry_SkipsWithoutTouchingTheDatabase()
    {
        // Arrange
        var context = CreateContext(MakeFunction("public", "list_changes"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    [Fact]
    public async Task ExecuteAsync_DestinationConnectionUnavailable_FailsTheStage()
    {
        // Arrange
        _connectionProvider.GetDestinationConnectionAsync(
                Arg.Any<CopyContext>(),
                Arg.Any<Microsoft.Extensions.Logging.ILogger>(),
                Arg.Any<CancellationToken>())
            .Returns((NpgsqlConnection?)null);
        var context = CreateContext(MakeFunction("public", "list_changes"));
        MarkFunctionsFailed(context, 0);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeFalse();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.ConnectionFailed);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private static void MarkFunctionsFailed(CopyContext context, params int[] indices) =>
        context.Properties["FailedFunctionIndices"] = indices.ToList();

    private static CopyContext CreateContext(params FunctionDefinition[] functions)
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
            Functions: functions,
            Triggers: [],
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

    private static FunctionDefinition MakeFunction(
        string schema,
        string name,
        params string[] setParameters) =>
        new(schema,
            name,
            "plpgsql",
            "void",
            $"CREATE FUNCTION {schema}.{name}() RETURNS void AS $$ BEGIN END $$ LANGUAGE plpgsql",
            [],
            EFunctionVolatility.Volatile,
            false,
            false,
            null,
            false)
        {
            SetParameters = setParameters
        };
}
