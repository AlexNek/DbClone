using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

using DbClone.UI.ViewModels;

using FluentAssertions;

using NSubstitute;

using UI.Tests.Fakes;

namespace UI.Tests.ViewModels;

/// <summary>
/// Unit tests for the text/clipboard import path of UnifiedConnectionManagerViewModel:
/// an import always reports what it assumed or why it imported nothing.
/// </summary>
public class UnifiedConnectionManagerImportTests
{
    [Fact]
    public void ImportFromText_WhenImportWarns_ReportsTheWarningsAndSaves()
    {
        // Arrange
        var connectionStore = new InMemoryConnectionStore();
        var importService = Substitute.For<IConnectionImportService>();
        importService.Import(Arg.Any<string>()).Returns(
            new ImportResult
            {
                Success = true,
                Connection = ImportedConnection(),
                Warnings =
                [
                    new ImportWarning(
                        EWarningLevel.Warning,
                        "Port is not specified; the default 5432 will be used.",
                        "Port")
                ]
            });
        var vm = CreateViewModel(connectionStore, importService);

        // Act
        vm.ImportFromText("Host=test.example.com;Database=testdb;Username=test_user");

        // Assert
        vm.ImportMessage.Should().Contain("Port is not specified");
        connectionStore.GetAll().Should().HaveCount(1);
    }

    [Fact]
    public void ImportFromText_WhenImportIsClean_ReportsNothing()
    {
        // Arrange
        var connectionStore = new InMemoryConnectionStore();
        var importService = Substitute.For<IConnectionImportService>();
        importService.Import(Arg.Any<string>()).Returns(
            new ImportResult { Success = true, Connection = ImportedConnection() });
        var vm = CreateViewModel(connectionStore, importService);

        // Act
        vm.ImportFromText("Host=test.example.com;Port=5432;Database=testdb;Username=test_user");

        // Assert
        vm.ImportMessage.Should().BeNull();
        connectionStore.GetAll().Should().HaveCount(1);
    }

    [Fact]
    public void ImportFromText_WhenNothingIsRecognized_ReportsItAndSavesNothing()
    {
        // Arrange — the service throws when no format claims the text
        var connectionStore = new InMemoryConnectionStore();
        var importService = Substitute.For<IConnectionImportService>();
        importService.Import(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException());
        var vm = CreateViewModel(connectionStore, importService);

        // Act
        vm.ImportFromText("not a connection string at all");

        // Assert
        vm.ImportMessage.Should().NotBeNullOrWhiteSpace();
        connectionStore.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void ImportFromText_WhenImportFailed_ReportsTheFailureReason()
    {
        // Arrange
        var connectionStore = new InMemoryConnectionStore();
        var importService = Substitute.For<IConnectionImportService>();
        importService.Import(Arg.Any<string>()).Returns(
            ImportResult.Failed(
                [new ImportWarning(EWarningLevel.Error, "Parse failed: bad URI.")]));
        var vm = CreateViewModel(connectionStore, importService);

        // Act
        vm.ImportFromText("postgresql://test.example.com:0/testdb");

        // Assert
        vm.ImportMessage.Should().Contain("Parse failed");
        connectionStore.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void ImportFromText_ClearsThePreviousReport()
    {
        // Arrange — a first import leaves a report behind
        var connectionStore = new InMemoryConnectionStore();
        var importService = Substitute.For<IConnectionImportService>();
        var vm = CreateViewModel(connectionStore, importService);
        importService.Import(Arg.Any<string>()).Returns(
            ImportResult.Failed([new ImportWarning(EWarningLevel.Error, "Parse failed.")]),
            new ImportResult { Success = true, Connection = ImportedConnection() });

        // Act
        vm.ImportFromText("Host=test.example.com");
        vm.ImportFromText("Host=test.example.com;Port=5432;Database=testdb");

        // Assert — the second import replaces the first report
        vm.ImportMessage.Should().BeNull();
    }

    private static UnifiedConnectionManagerViewModel CreateViewModel(
        InMemoryConnectionStore connectionStore,
        IConnectionImportService importService) =>
        new(
            connectionStore,
            new InMemoryConnectionGroupStore(),
            Substitute.For<IConnectionTextService>(),
            Substitute.For<IServerAdminService>(),
            importService,
            Substitute.For<IConnectionExportService>(),
            Substitute.For<IBackupEncryptionService>(),
            TestPlatformResolver.Create());

    private static DatabaseConnection ImportedConnection() =>
        new()
        {
            Host = "test.example.com",
            Port = 5432,
            Database = "testdb",
            Username = "test_user",
            Provider = "postgresql"
        };
}
