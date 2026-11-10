using CanonicalModel.Enums;

using DbClone.UI.StoreRestore.Handlers;
using DbClone.UI.StoreRestore.Workflows;
using DbClone.UI.ViewModels;

using FluentAssertions;

using Xunit;

namespace DbClone.UI.Tests;

/// <summary>
/// D6: the handler turns a store result into log lines. The summary file is named by its path, a report
/// the user asked for and did not get is stated as a warning, and a report the user never asked for is
/// not mentioned at all — the three cases the log has to keep apart.
/// </summary>
/// <remarks>
/// <see cref="WorkflowState"/> is used as it is: the handler's whole surface is the state it writes, so
/// no view model and no UI thread is involved.
/// </remarks>
public sealed class WorkflowResultHandlerTests
{
    private const string CatalogPath = @"C:\temp\testdb.catalog.rdbx";

    private const string SummaryPath = CatalogPath + "\\summary.md";

    private readonly WorkflowResultHandler _sut = new();

    [Fact]
    public void HandleStoreResult_LogsTheSummaryPathUnderTheCatalogPath()
    {
        // Arrange
        var state = new WorkflowState();

        // Act
        _sut.HandleStoreResult(state, StoreResult(withSummary: true));

        // Assert (FR12) — the user is told where the file is, as a detail line beside the catalog's.
        Messages(state).Should().ContainInOrder(
            "Store succeeded: 2 tables, 12 rows, summary written",
            $"        Catalog: {CatalogPath}",
            $"        Summary: {SummaryPath}");
    }

    [Fact]
    public void HandleStoreResult_WarnsWhenTheSummaryWasAskedForAndNotWritten()
    {
        // Arrange — the shape D4.2 leaves behind: the store finished, the report stage was cancelled.
        var state = new WorkflowState();

        // Act
        _sut.HandleStoreResult(state, StoreResult(withSummary: false, summaryRequested: true));

        // Assert — one warning that names the missing file, and nothing that blames the catalog.
        state.LogMessages.Should().ContainSingle(entry => entry.Level == ELogLevel.Warning)
            .Which.Message.Should().Be("Summary: not written");
        state.StatusBarSummary.Should().Be("Store succeeded — 2 tables");
        state.LastError.Should().BeEmpty("a stopped optional stage is not a failure");
    }

    [Fact]
    public void HandleStoreResult_SaysNothingAboutTheSummary_WhenTheOptionWasOff()
    {
        // Arrange
        var state = new WorkflowState();

        // Act
        _sut.HandleStoreResult(state, StoreResult(withSummary: false, summaryRequested: false));

        // Assert — a feature the user switched off must not read like a problem.
        Messages(state).Should().NotContain(message => message.Contains("Summary", StringComparison.Ordinal));
        state.LogMessages.Should().NotContain(entry => entry.Level == ELogLevel.Warning);
    }

    [Fact]
    public void HandleStoreResult_KeepsTheWarningForAFailedReportWrite_BesideTheCatalogLine()
    {
        // Arrange — FR13 arrives as an issue, so the store is complete with issues and has no path.
        var state = new WorkflowState();
        var result = StoreResult(withSummary: false, summaryRequested: true) with
        {
            HasIssues = true,
            Message = "Store completed with issues: 2 tables",
        };

        // Act
        _sut.HandleStoreResult(state, result);

        // Assert — the warning about the summary is still there, and the banner is the one that carries
        // the issue, which is why no new member was invented to route the issue text here. The store
        // itself stays a success: an issue about a courtesy file does not undo a catalog.
        Messages(state).Should().Contain("Summary: not written");
        state.StatusBarSummary.Should().Be("Store succeeded — 2 tables");
        state.BannerSeverity.Should().Be(Wpf.Ui.Controls.InfoBarSeverity.Warning);
    }

    private static StoreResult StoreResult(bool withSummary, bool summaryRequested = true) =>
        new(
            Success: true,
            Message: withSummary
                ? "Store succeeded: 2 tables, 12 rows, summary written"
                : "Store succeeded: 2 tables, 12 rows",
            CatalogPath: CatalogPath,
            TablesExported: 2,
            RowsExported: 12,
            ElapsedSeconds: 1.5,
            HasIssues: false,
            VerificationPassed: true,
            SummaryPath: withSummary ? SummaryPath : null,
            SummaryRequested: summaryRequested);

    private static IEnumerable<string> Messages(WorkflowState state) =>
        state.LogMessages.Select(entry => entry.Message);
}
