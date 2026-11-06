using FluentAssertions;

using Npgsql;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The two settings the block relies on but the connection text crossing the boundary does not
/// carry: a re-parse of the target's string drops both, so every string built from a target
/// re-applies them.
/// </summary>
public class TargetConnectionStringTests
{
    [Fact]
    public void ParseConnectionString_ReAppliesConnectTimeout()
    {
        // Act
        var connectionString = PgConnectionStringBuilder
            .ParseConnectionString(TestTargets.Local())
            .ConnectionString;

        // Assert
        connectionString.Should().Contain("Timeout=30");
        new NpgsqlConnectionStringBuilder(connectionString).Timeout.Should().Be(30);
    }

    [Fact]
    public void BuildCopyConnectionString_ReAppliesConnectTimeoutAndKeepAlive()
    {
        // Act
        var connectionString = PgConnectionStringBuilder
            .BuildCopyConnectionString(TestTargets.Local());

        // Assert
        connectionString.Should().Contain("Timeout=30");
        connectionString.Should().Contain("Keepalive=30");

        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
        parsed.KeepAlive.Should().Be(30);
    }

    [Fact]
    public void DatabaseOverrideBuiltFromTarget_ReAppliesConnectTimeout()
    {
        // The maintenance calls connect to a database other than the target's own, so the
        // override string is rebuilt from the target profile.
        // Act
        var target = TestTargets.Local();
        var connectionString = PgConnectionStringBuilder
            .BuildConnectionString(PgConnectionStringBuilder.ToConnectionInfo(target), "postgres")
            .ConnectionString;

        // Assert
        connectionString.Should().Contain("Timeout=30");
        connectionString.Should().Contain("Database=postgres");
    }

    [Fact]
    public void TargetEndpoint_IsReadBackOutOfTheConnectionText()
    {
        // Assert the three values the log lines print survive the boundary
        PgConnectionStringBuilder
            .Describe(TestTargets.Local(database: "srcdb"))
            .Should().Be("test.example.com:5432/srcdb");
    }
}
