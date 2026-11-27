using CanonicalModel.DTOs;

using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;

using DbClone.UI.ViewModels;

using FluentAssertions;

using NSubstitute;

namespace UI.Tests.ViewModels;

/// <summary>
/// Unit tests for ImportConnectionViewModel: the dialog only asks whether a format
/// understood the text, never whether every field was present in it.
/// </summary>
public class ImportConnectionViewModelTests
{
    [Fact]
    public void CanImport_StaysEnabled_WhenTheImportHadToFillInDefaults()
    {
        // Arrange — the text carried no port; the import applied one and said so
        var importService = Substitute.For<IConnectionImportService>();
        importService.Detect(Arg.Any<string>()).Returns(Detected);
        importService.Import(Arg.Any<string>()).Returns(
            new ImportResult
            {
                Success = true,
                Connection = new DatabaseConnection { Host = "test.example.com", Port = 5432 },
                Warnings =
                [
                    new ImportWarning(
                        EWarningLevel.Warning,
                        "Port is not specified; the default 5432 will be used.",
                        "Port")
                ]
            });
        var vm = new ImportConnectionViewModel(importService);

        // Act
        vm.InputText = "Host=test.example.com;Database=testdb";

        // Assert
        vm.CanImport.Should().BeTrue();
        vm.DetectionState.Should().Be("warning");
    }

    [Fact]
    public void CanImport_StaysEnabled_WhenTheTextNamesNoHost()
    {
        // Arrange — an inferred host arrives as localhost plus a warning
        var importService = Substitute.For<IConnectionImportService>();
        importService.Detect(Arg.Any<string>()).Returns(Detected);
        importService.Import(Arg.Any<string>()).Returns(
            new ImportResult
            {
                Success = true,
                Connection = new DatabaseConnection { Host = "localhost", Port = 5432 },
                Warnings =
                [
                    new ImportWarning(
                        EWarningLevel.Warning,
                        "Host is not specified; the default localhost will be used.",
                        "Host")
                ]
            });
        var vm = new ImportConnectionViewModel(importService);

        // Act
        vm.InputText = "Database=testdb;Username=test_user";

        // Assert
        vm.CanImport.Should().BeTrue();
        vm.DetectionState.Should().Be("warning");
    }

    [Fact]
    public void CanImport_IsEnabled_WhenTheImportIsComplete()
    {
        // Arrange
        var importService = Substitute.For<IConnectionImportService>();
        importService.Detect(Arg.Any<string>()).Returns(Detected);
        importService.Import(Arg.Any<string>()).Returns(
            new ImportResult
            {
                Success = true,
                Connection = new DatabaseConnection { Host = "test.example.com", Port = 5432 }
            });
        var vm = new ImportConnectionViewModel(importService);

        // Act
        vm.InputText = "Host=test.example.com;Port=5432;Database=testdb";

        // Assert
        vm.CanImport.Should().BeTrue();
        vm.DetectionState.Should().Be("success");
    }

    [Fact]
    public void CanImport_IsDisabled_WhenNoFormatClaimsTheText()
    {
        // Arrange
        var importService = Substitute.For<IConnectionImportService>();
        importService.Detect(Arg.Any<string>()).Returns(DetectionResult.None);
        var vm = new ImportConnectionViewModel(importService);

        // Act
        vm.InputText = "not a connection string at all";

        // Assert
        vm.CanImport.Should().BeFalse();
        vm.DetectionState.Should().Be("error");
    }

    [Fact]
    public void CanImport_IsDisabled_WhenTheClaimedFormatFailsToParse()
    {
        // Arrange
        var importService = Substitute.For<IConnectionImportService>();
        importService.Detect(Arg.Any<string>()).Returns(Detected);
        importService.Import(Arg.Any<string>()).Returns(
            ImportResult.Failed(
                [new ImportWarning(EWarningLevel.Error, "Parse failed: bad authority.")]));
        var vm = new ImportConnectionViewModel(importService);

        // Act
        vm.InputText = "postgresql://test.example.com:0/testdb";

        // Assert
        vm.CanImport.Should().BeFalse();
        vm.DetectionState.Should().Be("error");
    }

    private static readonly DetectionResult Detected =
        new(true, "test-provider", "test-format", "Test Format", 1.0, "test-source");
}
