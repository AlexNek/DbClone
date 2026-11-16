using DbAdapter.Definitions;
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
/// Hermetic tests for <see cref="CreateFunctionsStage"/>: the executor is substituted and the
/// destination connection is constructed but never opened. The stage asks for the SET privileges a
/// function declares before it creates it, and a refusal still leaves every function attempted.
/// </summary>
public class CreateFunctionsStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();

    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();

    private readonly IPgConnectionProvider _connectionProvider = Substitute.For<IPgConnectionProvider>();

    private readonly List<string> _statements = [];

    private readonly CreateFunctionsStage _stage;

    public CreateFunctionsStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _connectionProvider.GetDestinationConnectionAsync(
                Arg.Any<CopyContext>(),
                Arg.Any<Microsoft.Extensions.Logging.ILogger>(),
                Arg.Any<CancellationToken>())
            .Returns(DestConnection);
        _executor.ExecuteScalarOrNullAsync<string>("SELECT current_user", Arg.Any<CancellationToken>())
            .Returns("test_user");
        _executor.ExecuteNonQueryAsync(Arg.Do<string>(sql => _statements.Add(sql)), Arg.Any<CancellationToken>())
            .Returns(0);

        _stage = new CreateFunctionsStage(
            new PgDdlGenerator(),
            _factory,
            _connectionProvider,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_FunctionDeclaresAGrantableParameter_GrantsItBeforeTheFunctionExists()
    {
        // Arrange
        var context = CreateContext(MakeFunction("realtime", "list_changes", "log_min_messages"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        var grant = _statements.Should()
                             .ContainSingle(sql => sql.StartsWith("GRANT", StringComparison.Ordinal)).Subject;
        grant.Should().Be("GRANT SET ON PARAMETER log_min_messages TO test_user");

        var firstFunction = _statements.FindIndex(sql => sql.StartsWith("CREATE", StringComparison.Ordinal));
        _statements.IndexOf(grant)
            .Should()
            .BeLessThan(firstFunction, "the privilege is checked when the function is created");
        context.Statistics.FunctionsCopied.Should().Be(1);

        var statistic = result.Details.Single(d => d.Kind == EStageMessageKind.Statistic);
        statistic.Get<string>(PropKeys.Reason).Should().Be("SET privileges granted");
        statistic.Get<int>(PropKeys.Count).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ParameterOutsideTheAllowList_IsNeverSentToTheServer()
    {
        // Arrange — a foreign function asking for a parameter that would change what it may do
        var context = CreateContext(MakeFunction("public", "list_changes", "search_path"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        _statements.Should().NotContain(sql => sql.Contains("GRANT", StringComparison.Ordinal));
        result.Details.Should().NotContain(d => d.Kind == EStageMessageKind.Statistic);
        context.Statistics.FunctionsCopied.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_DestinationBelowPostgreSql15_AttemptsNoGrant()
    {
        // Arrange — the statement does not exist before 15, so the destination's version decides
        var context = CreateContext(14, MakeFunction("realtime", "list_changes", "log_min_messages"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        _statements.Should().NotContain(sql => sql.Contains("GRANT", StringComparison.Ordinal));
        context.Statistics.FunctionsCopied.Should().Be(1);

        var statistic = result.Details.Single(d => d.Kind == EStageMessageKind.Statistic);
        statistic.Get<int>(PropKeys.Count).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_GrantRefused_StillAttemptsEveryFunction()
    {
        // Arrange
        RefuseGrants();
        var context = CreateContext(
            MakeFunction("realtime", "list_changes", "log_min_messages"),
            MakeFunction("public", "no_settings"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("a refused privilege is not a failed run");
        context.Statistics.FunctionsCopied.Should().Be(2);
        result.Details.Count(d => d.Kind == EStageMessageKind.Created).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_GrantRefusedAndFunctionRefused_StatesTheStatementToRun()
    {
        // Arrange
        RefuseGrants();
        _executor.ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql != null && sql.StartsWith("CREATE", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(RefusedParameter());

        var context = CreateContext(MakeFunction("realtime", "list_changes", "log_min_messages"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        var deferred = result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Deferred).Subject;
        deferred.Get<string>(PropKeys.Reason)
            .Should()
            .Be(
                "permission denied to set parameter \"log_min_messages\" — run on the destination: " +
                "GRANT SET ON PARAMETER log_min_messages TO test_user");
        context.Properties.Should().ContainKey(PgSetParameterGrant.RefusedSetParametersKey);
    }

    [Fact]
    public async Task ExecuteAsync_FunctionFailsForAnotherReason_KeepsTheServerMessage()
    {
        // Arrange — the function declares a refused parameter, but the server refused something else
        RefuseGrants();
        _executor.ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql != null && sql.StartsWith("CREATE", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("type \"missing_type\" does not exist"));

        var context = CreateContext(MakeFunction("realtime", "list_changes", "log_min_messages"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        var deferred = result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Deferred).Subject;
        deferred.Get<string>(PropKeys.Reason).Should().Be("type \"missing_type\" does not exist");
    }

    [Fact]
    public async Task ExecuteAsync_NoFunctionSetsAParameter_TouchesNoPrivilege()
    {
        // Arrange
        var context = CreateContext(MakeFunction("public", "no_settings"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        _statements.Should().NotContain(sql => sql.Contains("GRANT", StringComparison.Ordinal));
        result.Details.Should().NotContain(d => d.Kind == EStageMessageKind.Statistic);
        await _executor.DidNotReceive()
            .ExecuteScalarOrNullAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RoleCannotBeRead_StillCreatesEveryFunctionUnderCurrentUser()
    {
        // Arrange — reading the role is best effort: CURRENT_USER names the same role, whereas a
        // pre-flight that throws would fail the run and lose every function without a warning
        _executor.ExecuteScalarOrNullAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("connection lost"));
        var context = CreateContext(
            MakeFunction("realtime", "list_changes", "log_min_messages"),
            MakeFunction("public", "no_settings"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        context.Statistics.FunctionsCopied.Should().Be(2);
        _statements.Should().Contain("GRANT SET ON PARAMETER log_min_messages TO CURRENT_USER");
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
        var context = CreateContext(MakeFunction("public", "no_settings"));

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeFalse();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.ConnectionFailed);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    [Theory]
    [InlineData(ECopyMode.Resume)]
    [InlineData(ECopyMode.Update)]
    public async Task ExecuteAsync_ResumeOrUpdateMode_SkipsEntireStage(ECopyMode mode)
    {
        // Arrange
        var context = CreateContext(MakeFunction("realtime", "list_changes", "log_min_messages"), mode);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped);
        await _connectionProvider.DidNotReceive()
            .GetDestinationConnectionAsync(
                Arg.Any<CopyContext>(),
                Arg.Any<Microsoft.Extensions.Logging.ILogger>(),
                Arg.Any<CancellationToken>());
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private void RefuseGrants() =>
        _executor.ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql != null && sql.StartsWith("GRANT", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(RefusedParameter());

    private static PostgresException RefusedParameter() =>
        new("permission denied to set parameter \"log_min_messages\"", "ERROR", "ERROR", "42501");

    private static CopyContext CreateContext(
        params FunctionDefinition[] functions) =>
        CreateContext(17, ECopyMode.Full, functions);

    private static CopyContext CreateContext(
        FunctionDefinition function,
        ECopyMode mode) =>
        CreateContext(17, mode, function);

    private static CopyContext CreateContext(
        int destinationMajorVersion,
        params FunctionDefinition[] functions) =>
        CreateContext(destinationMajorVersion, ECopyMode.Full, functions);

    private static CopyContext CreateContext(
        int destinationMajorVersion,
        ECopyMode mode,
        params FunctionDefinition[] functions)
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
        var request = new CopyRequest(target, target, model, new CopyOptions(CopyMode: mode));
        return new CopyContext
        {
            Request = request,
            SourceModel = model,
            DestinationConnection = new PgCopyConnection(DestConnection, string.Empty),
            DestinationCapabilities = new ServerCapabilities(
                ServerVersion: $"{destinationMajorVersion}.4",
                IsSuperuser: false,
                SupportsBinaryCopy: true,
                SupportsPartitioning: true,
                SupportsIdentityColumns: true,
                SupportsGeneratedColumns: true,
                SupportsLogicalReplication: false,
                SupportsRowLevelSecurity: true,
                SupportsSessionReplicationRole: true,
                InstalledExtensions: [])
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
