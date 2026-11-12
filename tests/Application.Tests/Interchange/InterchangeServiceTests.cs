using CanonicalModel.Interchange;

using DatabaseInterchange.Transform;

using DbAdapter.Definitions;

using DbClone.Application.DTOs;
using DbClone.Application.Interchange;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;

using FluentAssertions;

using NSubstitute;

using Microsoft.Extensions.Logging;

using ImportResult = DatabaseInterchange.Transform.ImportResult;

namespace Application.Tests.Interchange;

/// <summary>
/// Unit tests for <see cref="InterchangeService"/>. These tests exercise the implemented facade
/// contract and keep asynchronous assertions observable by the test runner.
/// </summary>
public class InterchangeServiceTests
{
    private readonly DatabaseTargetFactory _targetFactory;
    private readonly ILogger<InterchangeService> _logger;
    private readonly InterchangeService _sut;

    public InterchangeServiceTests()
    {
        var adapter = Substitute.For<DbAdapter.Definitions.IDatabaseAdapter>();
        adapter.Info.Returns(new DbAdapter.Definitions.AdapterInfo("test.adapter", "testdb", "*", "1.0.0"));
        adapter.Syntax.Returns(Substitute.For<DbAdapter.Definitions.IConnectionStringSyntax>());
        adapter.Syntax.BuildKeyValue(Arg.Any<CanonicalModel.DTOs.ConnectionStringFields>())
            .Returns("Host=test.example.com;Port=15432;Database=testdb;Username=test_user");

        var catalog = Substitute.For<DbClone.Application.Adapters.IAdapterCatalog>();
        catalog.All.Returns(new[] { adapter });
        catalog.Find(Arg.Any<string>()).Returns(adapter);

        _targetFactory = new DatabaseTargetFactory(catalog);
        _logger = Substitute.For<ILogger<InterchangeService>>();
        _sut = new InterchangeService(_targetFactory, _logger);
    }

    [Fact]
    public async Task StoreAsync_validates_request_not_null()
    {
        Func<Task> act = () => _sut.StoreAsync(null!, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PlanRestoreAsync_validates_catalog_path_not_empty()
    {
        Func<Task> act = () => _sut.PlanRestoreAsync(string.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RestoreAsync_validates_request_not_null()
    {
        Func<Task> act = () => _sut.RestoreAsync(null!, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task InspectAsync_reports_missing_catalog_as_an_error()
    {
        Func<Task> act = () => _sut.InspectAsync(Path.Combine(Path.GetTempPath(), "missing-test-catalog"), CancellationToken.None);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public void BuildRestoreResult_ImportSucceededWithoutValidation_ReportsSuccess()
    {
        // Arrange
        var result = new ImportResult { Success = true, ObjectsApplied = 12 };

        // Act
        var view = InterchangeService.BuildRestoreResult(result, TimeSpan.FromSeconds(3));

        // Assert
        view.Outcome.Should().Be(ERestoreOutcome.Success);
        view.ObjectsApplied.Should().Be(12);
        view.RowCountMismatches.Should().Be(0);
        view.Issues.Should().BeEmpty();
        view.Validation.Should().BeNull();
        view.Elapsed.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void BuildRestoreResult_ValidationPassed_ReportsSuccessAndKeepsTheReport()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            ObjectsApplied = 12,
            ValidationReport = new ImportValidationReport { Passed = true },
        };

        // Act
        var view = InterchangeService.BuildRestoreResult(result, TimeSpan.FromSeconds(3));

        // Assert
        view.Outcome.Should().Be(ERestoreOutcome.Success);
        view.Validation!.Passed.Should().BeTrue();
        view.Validation.Issues.Should().BeEmpty();
    }

    [Fact]
    public void BuildRestoreResult_RowCountMismatches_AreReportedToTheUser()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            ObjectsApplied = 12,
            ValidationReport = new ImportValidationReport
            {
                Passed = false,
                Issues =
                [
                    new InterchangeIssue("RowCountMismatch", EIssueSeverity.Error,
                        "The catalog records 5 row(s) for public.customers, but the target holds 3."),
                    new InterchangeIssue("RowCountMismatch", EIssueSeverity.Error,
                        "The catalog records 2 row(s) for sales.orders, but the target holds 0."),
                ],
            },
        };

        // Act
        var view = InterchangeService.BuildRestoreResult(result, TimeSpan.FromSeconds(3));

        // Assert — a restore that did not carry the rows is never reported as a plain success, and
        // each mismatch reaches the log: the UI reads the issues list, not the Validation summary.
        view.Outcome.Should().Be(ERestoreOutcome.CompletedWithIssues);
        view.RowCountMismatches.Should().Be(2);
        view.Issues.Should().HaveCount(2);
        view.Issues.Select(i => i.Sentence).Should().Contain(
            "The catalog records 5 row(s) for public.customers, but the target holds 3.");
        view.Issues.Should().OnlyContain(i => i.Severity == EInterchangeSeverity.Error);
    }

    [Fact]
    public void BuildRestoreResult_ValidationWarning_IsReportedWithoutACountMismatch()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            ValidationReport = new ImportValidationReport
            {
                Passed = true,
                Issues =
                [
                    new InterchangeIssue("ValidationKindNotChecked", EIssueSeverity.Warning,
                        "The catalog records 1 object(s) of kind Collation, so the target was not checked for it."),
                ],
            },
        };

        // Act
        var view = InterchangeService.BuildRestoreResult(result, TimeSpan.FromSeconds(3));

        // Assert
        view.Outcome.Should().Be(ERestoreOutcome.CompletedWithIssues);
        view.RowCountMismatches.Should().Be(0);
        view.Issues.Should().ContainSingle()
            .Which.Severity.Should().Be(EInterchangeSeverity.Warning);
    }

    [Fact]
    public void BuildRestoreResult_ImportFailed_ReportsFailedEvenWhenValidationPassed()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = false,
            ObjectsApplied = 4,
            Issues = [new InterchangeIssue("ImportFailed", EIssueSeverity.Error, "The write stopped.")],
            ValidationReport = new ImportValidationReport { Passed = true },
        };

        // Act
        var view = InterchangeService.BuildRestoreResult(result, TimeSpan.FromSeconds(3));

        // Assert
        view.Outcome.Should().Be(ERestoreOutcome.Failed);
        view.Issues.Should().ContainSingle().Which.Code.Should().Be("ImportFailed");
    }

    [Fact]
    public void BuildRestoreResult_ImportAndValidationIssues_AreBothReported()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            Issues = [new InterchangeIssue("SequenceOwnershipNotCaptured", EIssueSeverity.Warning,
                "The sequence lost its ownership link.",
                ObjectRef.Sequence("public", "customers_id_seq"),
                "The target sequence was recreated without its source ownership metadata.")],
            ValidationReport = new ImportValidationReport
            {
                Passed = false,
                Issues = [new InterchangeIssue("ObjectCountMismatch", EIssueSeverity.Error,
                    "The catalog records 3 object(s) of kind Table, but the target holds 2.")],
            },
        };

        // Act
        var view = InterchangeService.BuildRestoreResult(result, TimeSpan.FromSeconds(3));

        // Assert — the import's own notes come first, then what the target was found to hold
        view.Outcome.Should().Be(ERestoreOutcome.CompletedWithIssues);
        view.Issues.Select(i => i.Code).Should().Equal(
            "SequenceOwnershipNotCaptured", "ObjectCountMismatch");
        view.Issues[0].Location.Should().Be("sequence:public.customers_id_seq");
        view.Issues[0].Details.Should().Contain("ownership metadata");
    }
}
