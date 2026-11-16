using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Pipeline;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Hermetic tests for <see cref="PgSetParameterGrant"/>: no server and no connection string. The
/// policy is the point of these tests — only the five logging parameters may ever reach the server,
/// the destination's version decides whether the statement survives, and a refusal is an outcome
/// rather than an exception.
/// </summary>
public class PgSetParameterGrantTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();

    [Fact]
    public void AllowedParameters_NamesOnlyTheLoggingSettings()
    {
        PgSetParameterGrant.AllowedParameters.Should().BeEquivalentTo(
            [
                "log_min_messages",
                "log_min_error_statement",
                "log_min_duration_statement",
                "log_duration",
                "log_statement"
            ]);
    }

    [Fact]
    public void CandidateParameters_ParameterOutsideTheList_IsNeverACandidate()
    {
        // A foreign database choosing session_authorization or search_path must not reach the server
        var candidates = PgSetParameterGrant.CandidateParameters(
            ["log_min_messages", "search_path", "session_authorization"]);

        candidates.Should().Equal("log_min_messages");
    }

    [Fact]
    public void CandidateParameters_RepeatedAndPadded_IsOneCandidate()
    {
        var candidates = PgSetParameterGrant.CandidateParameters(
            ["log_min_messages", " log_min_messages ", "", "log_duration"]);

        candidates.Should().Equal("log_min_messages", "log_duration");
    }

    [Fact]
    public void CandidateParameters_NothingDeclared_IsEmpty()
    {
        PgSetParameterGrant.CandidateParameters(null).Should().BeEmpty();
        PgSetParameterGrant.CandidateParameters([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("15.0", true)]
    [InlineData("15.4 (Debian 15.4-1.pgdg120+1)", true)]
    [InlineData("17.2", true)]
    [InlineData("14.9", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SupportsGrants_DestinationVersionDecides(string? version, bool expected)
    {
        PgSetParameterGrant.SupportsGrants(version).Should().Be(expected);
    }

    [Fact]
    public async Task GrantAsync_AllowedParameter_IsGrantedToTheCopyRole()
    {
        // Arrange
        _executor.ExecuteScalarOrNullAsync<string>("SELECT current_user", Arg.Any<CancellationToken>())
            .Returns("test_user");
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);

        // Act
        var outcomes = await PgSetParameterGrant.GrantAsync(
                           _executor,
                           ["log_min_messages"],
                           NullLogger.Instance,
                           CancellationToken.None);

        // Assert
        var outcome = outcomes.Should().ContainSingle().Subject;
        outcome.Parameter.Should().Be("log_min_messages");
        outcome.Granted.Should().BeTrue();
        outcome.Statement.Should().Be("GRANT SET ON PARAMETER log_min_messages TO test_user");
        await _executor.Received(1)
            .ExecuteNonQueryAsync("GRANT SET ON PARAMETER log_min_messages TO test_user", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantAsync_RefusedGrant_IsReturnedAndNeverThrown()
    {
        // Arrange
        _executor.ExecuteScalarOrNullAsync<string>("SELECT current_user", Arg.Any<CancellationToken>())
            .Returns("test_user");
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PostgresException("permission denied", "ERROR", "ERROR", "42501"));

        // Act
        var outcomes = await PgSetParameterGrant.GrantAsync(
                           _executor,
                           ["log_min_messages"],
                           NullLogger.Instance,
                           CancellationToken.None);

        // Assert
        var outcome = outcomes.Should().ContainSingle().Subject;
        outcome.Granted.Should().BeFalse();
        outcome.Statement.Should().Contain("GRANT SET ON PARAMETER log_min_messages");
        outcome.Reason.Should().Be("permission denied");
    }

    [Fact]
    public async Task GrantAsync_RoleCannotBeRead_FallsBackToCurrentUser()
    {
        // Arrange — the scalar answers null, which is an ordinary answer on this path
        _executor.ExecuteScalarOrNullAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);

        // Act
        var outcomes = await PgSetParameterGrant.GrantAsync(
                           _executor,
                           ["log_duration"],
                           NullLogger.Instance,
                           CancellationToken.None);

        // Assert
        outcomes.Should().ContainSingle().Which.Statement
                .Should().Be("GRANT SET ON PARAMETER log_duration TO CURRENT_USER");
    }

    [Fact]
    public async Task GrantAsync_NoCandidate_TouchesNeitherTheServerNorTheRole()
    {
        var outcomes = await PgSetParameterGrant.GrantAsync(
                           _executor,
                           [],
                           NullLogger.Instance,
                           CancellationToken.None);

        outcomes.Should().BeEmpty();
        await _executor.DidNotReceive()
            .ExecuteScalarOrNullAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _executor.DidNotReceive()
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantAsync_Cancelled_PropagatesInsteadOfBecomingARefusal()
    {
        // Arrange — cancellation is never a server answer, so it must not be recorded as one
        _executor.ExecuteScalarOrNullAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("test_user");
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var act = () => PgSetParameterGrant.GrantAsync(
                            _executor,
                            ["log_min_messages"],
                            NullLogger.Instance,
                            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GrantAsync_RoleCannotBeReadAtAll_FallsBackToCurrentUserAndStillGrants()
    {
        // Arrange — the best-effort pre-flight must not throw: CURRENT_USER names the same role.
        _executor.ExecuteScalarOrNullAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("connection lost"));
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);

        // Act
        var outcomes = await PgSetParameterGrant.GrantAsync(
                           _executor,
                           ["log_min_messages"],
                           NullLogger.Instance,
                           CancellationToken.None);

        // Assert
        outcomes.Should().ContainSingle().Which.Statement
                .Should().Be("GRANT SET ON PARAMETER log_min_messages TO CURRENT_USER");
        await _executor.Received(1)
            .ExecuteNonQueryAsync(
                "GRANT SET ON PARAMETER log_min_messages TO CURRENT_USER",
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantAsync_RoleReadCancelled_PropagatesInsteadOfFallingBack()
    {
        // Arrange — cancellation is never a reason to invent a role
        _executor.ExecuteScalarOrNullAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var act = () => PgSetParameterGrant.GrantAsync(
                            _executor,
                            ["log_min_messages"],
                            NullLogger.Instance,
                            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void AdministratorStatementsFor_RefusedParameterAndInsufficientPrivilege_StatesTheStatement()
    {
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["log_min_messages"] = "GRANT SET ON PARAMETER log_min_messages TO test_user"
        };
        var denied = new PostgresException(
            "permission denied to set parameter \"log_min_messages\"",
            "ERROR",
            "ERROR",
            "42501");

        PgSetParameterGrant.AdministratorStatementsFor(denied, ["log_min_messages"], refused)
            .Should()
            .Equal("GRANT SET ON PARAMETER log_min_messages TO test_user");

        PgSetParameterGrant.DescribeFailure(denied, ["log_min_messages"], refused)
            .Should()
            .Be(
                "permission denied to set parameter \"log_min_messages\" — run on the destination: " +
                "GRANT SET ON PARAMETER log_min_messages TO test_user");
    }

    [Fact]
    public void AdministratorStatementsFor_TwoDeclaredParametersRefused_StatesBothInDeclarationOrder()
    {
        // The server names one of them; the run knows the statement for both
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["log_duration"] = "GRANT SET ON PARAMETER log_duration TO test_user",
            ["log_min_messages"] = "GRANT SET ON PARAMETER log_min_messages TO test_user"
        };

        PgSetParameterGrant.AdministratorStatementsFor(
                new PostgresException("permission denied", "ERROR", "ERROR", "42501"),
                ["log_min_messages", "log_duration"],
                refused)
            .Should()
            .Equal(
                "GRANT SET ON PARAMETER log_min_messages TO test_user",
                "GRANT SET ON PARAMETER log_duration TO test_user");
    }

    [Fact]
    public void AdministratorStatementFor_AnotherCause_StatesNothing()
    {
        // A function that also declares a refused parameter must not blame the privilege for a
        // missing type — the advice would change nothing.
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["log_min_messages"] = "GRANT SET ON PARAMETER log_min_messages TO test_user"
        };

        PgSetParameterGrant.AdministratorStatementsFor(
                new InvalidOperationException("type \"missing_type\" does not exist"),
                ["log_min_messages"],
                refused)
            .Should()
            .BeEmpty();

        PgSetParameterGrant.AdministratorStatementsFor(
                new PostgresException("type does not exist", "ERROR", "ERROR", "42704"),
                ["log_min_messages"],
                refused)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void AdministratorStatementFor_FunctionThatDoesNotDeclareIt_StatesNothing()
    {
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["log_min_messages"] = "GRANT SET ON PARAMETER log_min_messages TO test_user"
        };

        PgSetParameterGrant.AdministratorStatementsFor(
                new PostgresException("permission denied", "ERROR", "ERROR", "42501"),
                ["log_duration"],
                refused)
            .Should()
            .BeEmpty();

        PgSetParameterGrant.AdministratorStatementsFor(
                new PostgresException("permission denied", "ERROR", "ERROR", "42501"),
                ["log_min_messages"],
                null)
            .Should()
            .BeEmpty();
    }
}
