using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.Services;
using DbClone.PostgreSql.Formats;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace Application.Tests;

public class ConnectionImportServiceTests
{
    [Fact]
    public void Detect_MatchingFormat_ReturnsDetected()
    {
        // Arrange
        var format = CreateFakeFormat(displayName: "My Format");
        var service = CreateService(format);

        // Act
        var detection = service.Detect("some input");

        // Assert
        detection.IsDetected.Should().BeTrue();
        detection.FormatDisplayName.Should().Be("My Format");
    }

    [Fact]
    public void Detect_NoMatchingFormat_ReturnsNone()
    {
        // Arrange
        var format = CreateFakeFormat(canImport: false);
        var service = CreateService(format);

        // Act
        var detection = service.Detect("some input");

        // Assert
        detection.IsDetected.Should().BeFalse();
    }

    [Fact]
    public void Detect_NpgsqlWithUnknownKey_StillDetectsFormat()
    {
        // Arrange
        var service = CreateServiceWithRealFormats();
        var input = "Hosta=test.example.com;Port=5432;Database=testdb;Username=test_user";

        // Act
        var detection = service.Detect(input);

        // Assert
        detection.IsDetected.Should().BeTrue();
        detection.FormatDisplayName.Should().Be("Npgsql / .NET");
    }

    // ── Unit tests: mock format ───────────────────────────────────────────

    [Fact]
    public void Import_ConnectionWithNoOptions_ProducesNoAdditionalParameterWarnings()
    {
        // Arrange
        var connection = new DatabaseConnection
                             {
                                 Host = "localhost",
                                 Port = 5432,
                                 Database = "db",
                                 Username = "user",
                                 Password = "pass"
                             };
        var format = CreateFakeFormat(parsedConnection: connection);
        var service = CreateService(format);

        // Act
        var result = service.Import("Host=localhost;Database=db");

        // Assert
        result.Success.Should().BeTrue();
        result.Warnings.Should().NotContain(w => w.Message.Contains("parameter preserved"));
    }

    [Fact]
    public void Import_EmptyInput_ReturnsFailed()
    {
        // Arrange
        var service = CreateService(CreateFakeFormat());

        // Act
        var result = service.Import("   ");

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void Import_MissingPassword_ProducesWarning()
    {
        // Arrange
        var connection = new DatabaseConnection
                             {
                                 Host = "localhost",
                                 Database = "db",
                                 Username = "user",
                                 Password = null
                             };
        var format = CreateFakeFormat(parsedConnection: connection);
        var service = CreateService(format);

        // Act
        var result = service.Import("some input");

        // Assert
        result.Warnings.Should().Contain(w =>
            w.Level == EWarningLevel.Warning && w.Message.Contains("Password is missing"));
    }

    [Fact]
    public void Import_NpgsqlNoExtraParams_ProducesNoAdditionalParameterWarnings()
    {
        // Arrange
        var service = CreateServiceWithRealFormats();
        var input =
            "Host=test.example.com;Port=5432;Database=testdb;Username=test_user;Password=pass";

        // Act
        var result = service.Import(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Warnings.Should().NotContain(w => w.Message.Contains("parameter preserved"));
    }

    [Fact]
    public void Import_NpgsqlWithExplicitTimeout_PreservesInOptions()
    {
        // Arrange
        var service = CreateServiceWithRealFormats();
        var input = "Host=localhost;Port=5432;Database=db;Username=user;Password=pass;Timeout=30";

        // Act
        var result = service.Import(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Connection!.Options.Should().ContainKey("Timeout").WhoseValue.Should().Be("30");
    }

    // ── Integration tests: real formats + real service ─────────────────────

    [Fact]
    public void Import_NpgsqlWithSearchPathOnly_NoTimeoutOrPoolingWarnings()
    {
        // Arrange
        var service = CreateServiceWithRealFormats();
        var input =
            "Host=test.example.com;Port=5432;Database=testdb;Username=test_user;Password=Xk9#mZ!qR4w;Search Path=public";

        // Act
        var result = service.Import(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Connection!.Options.Should().HaveCount(1);
        result.Connection.Options.Should().ContainKey("Search Path");
        result.Warnings.Should().NotContain(w => w.Message.Contains("Timeout"));
        result.Warnings.Should().NotContain(w => w.Message.Contains("Pooling"));
    }

    [Fact]
    public void Import_NpgsqlWithSpecialCharsInPassword_ParsesCorrectly()
    {
        // Arrange
        var service = CreateServiceWithRealFormats();
        var input =
            "Host=test.example.com;Port=5432;Database=testdb;Username=test_user;Password=Xk9#mZ!qR4w";

        // Act
        var result = service.Import(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Connection!.Host.Should().Be("test.example.com");
        result.Connection.Password.Should().Be("Xk9#mZ!qR4w");
        result.Connection.Options.Should().BeEmpty();
    }

    [Fact]
    public void Import_NpgsqlWithUnknownKey_ParsesValidKeysAndWarnsAboutInvalid()
    {
        // Arrange
        var service = CreateServiceWithRealFormats();
        var input =
            "Hosta=test.example.com;Port=5432;Database=testdb;Username=test_user;Password=Xk9#mZ!qR4w;Search Path=public";

        // Act
        var result = service.Import(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Connection!.Port.Should().Be(5432);
        result.Connection.Database.Should().Be("testdb");
        result.Connection.Username.Should().Be("test_user");
        result.Connection.Password.Should().Be("Xk9#mZ!qR4w");
        result.Connection.Options.Should().ContainKey("Search Path").WhoseValue.Should()
            .Be("public");
        result.Warnings.Should().Contain(w =>
            w.Level == EWarningLevel.Warning
            && w.Message.Contains("Unrecognized parameter ignored: Hosta"));
    }

    [Fact]
    public void Import_UriWithSpecialCharsInPassword_ParsesCorrectly()
    {
        // Arrange
        var service = CreateServiceWithRealFormats();
        var input = "postgresql://test_user:Xk9%23mZ!qR4w@test.example.com:5432/testdb";

        // Act
        var result = service.Import(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Connection!.Password.Should().Be("Xk9#mZ!qR4w");
        result.Connection.Host.Should().Be("test.example.com");
    }

    [Fact]
    public void Import_WarningMessages_NeverSayUnknown()
    {
        // Arrange
        var service = CreateServiceWithRealFormats();
        var input =
            "Host=test.example.com;Port=5432;Database=testdb;Username=test_user;Password=pass;Search Path=public;Timeout=15;Pooling=true";

        // Act
        var result = service.Import(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Warnings.Should().NotContain(w => w.Message.Contains("Unknown"));
    }

    private static IConnectionFormat CreateFakeFormat(
        string id = "test-format",
        string displayName = "Test Format",
        bool canImport = true,
        DatabaseConnection? parsedConnection = null)
    {
        var format = Substitute.For<IConnectionFormat>();
        format.Id.Returns(id);
        format.DisplayName.Returns(displayName);
        format.Provider.Returns(EDatabaseProvider.PostgreSql);
        format.DetectionPriority.Returns(10);
        format.TypicalSource.Returns("Test");
        format.CanImport(Arg.Any<string>()).Returns(canImport);
        format.Parse(Arg.Any<string>()).Returns(
            parsedConnection ?? new DatabaseConnection
                                    {
                                        Host = "localhost",
                                        Port = 5432,
                                        Database = "testdb",
                                        Username = "user",
                                        Password = "pass"
                                    });
        return format;
    }

    private static ConnectionImportService CreateService(params IConnectionFormat[] formats) =>
        new(formats, NullLogger<ConnectionImportService>.Instance);

    private static ConnectionImportService CreateServiceWithRealFormats() =>
        new(
                [
                    new PostgreSqlUriFormat(),
                    new PostgreSqlNpgsqlFormat(),
                    new PostgreSqlJdbcFormat(),
                    new PostgreSqlLibpqFormat(),
                ],
            NullLogger<ConnectionImportService>.Instance);
}
