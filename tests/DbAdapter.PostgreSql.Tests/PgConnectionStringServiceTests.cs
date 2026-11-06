using CanonicalModel.Enums;

using DbAdapter.PostgreSql.Providers;

using FluentAssertions;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Unit tests for <see cref="PgConnectionStringService"/>, covering the URI
/// parsing path and the "postgres" username default applied at the call site.
/// </summary>
public class PgConnectionStringServiceTests
{
    private readonly PgConnectionStringService _sut = new();

    [Fact]
    public void TryParse_UriWithoutUserInfo_DefaultsUsernameToPostgres()
    {
        // Arrange — URI without userinfo; the service must apply the "postgres" default
        var input = "postgresql://host.example.com:5432/mydb";

        // Act
        var success = _sut.TryParse(input, out var fields);

        // Assert
        success.Should().BeTrue();
        fields.Username.Should().Be("postgres");
        fields.Password.Should().BeEmpty();
        fields.Host.Should().Be("host.example.com");
        fields.Port.Should().Be(5432);
        fields.Database.Should().Be("mydb");
    }

    [Fact]
    public void TryParse_UriWithUserInfo_KeepsExplicitUsername()
    {
        // Arrange
        var input = "postgres://test_user:s3cret@host.example.com:5432/mydb";

        // Act
        var success = _sut.TryParse(input, out var fields);

        // Assert
        success.Should().BeTrue();
        fields.Username.Should().Be("test_user");
        fields.Password.Should().Be("s3cret");
    }

    [Theory]
    [InlineData("disable", ESslMode.Disable)]
    [InlineData("require", ESslMode.Require)]
    [InlineData("verify-ca", ESslMode.VerifyCA)]
    [InlineData("verify-full", ESslMode.VerifyFull)]
    public void TryParse_UriWithSslMode_MapsToSslModeName(string sslValue, ESslMode expected)
    {
        // Arrange — hyphenated URI values must be normalized to the enum name
        var input = $"postgresql://user:pass@host.example.com:5432/mydb?sslmode={sslValue}";

        // Act
        var success = _sut.TryParse(input, out var fields);

        // Assert
        success.Should().BeTrue();
        fields.SslMode.Should().Be(expected);
    }

    [Fact]
    public void TryParse_UriWithoutSslMode_DefaultsToPrefer()
    {
        // Arrange
        var input = "postgresql://user:pass@host.example.com:5432/mydb";

        // Act
        var success = _sut.TryParse(input, out var fields);

        // Assert
        success.Should().BeTrue();
        fields.SslMode.Should().Be(ESslMode.Prefer);
    }

    [Fact]
    public void TryParse_UriWithUnknownSslMode_DefaultsToPrefer()
    {
        // Arrange
        var input = "postgresql://user:pass@host.example.com:5432/mydb?sslmode=bogus";

        // Act
        var success = _sut.TryParse(input, out var fields);

        // Assert
        success.Should().BeTrue();
        fields.SslMode.Should().Be(ESslMode.Prefer);
    }
}
