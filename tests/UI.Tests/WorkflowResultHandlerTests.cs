using CanonicalModel.Enums;

using DbClone.Application.Interchange;
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

    private const string SchemaScriptPath = CatalogPath + "\\sql\\schema.sql";

    private const string DataScriptPath = CatalogPath + "\\sql\\data.sql";

    private const string FirstPartPath = CatalogPath + "\\sql\\data\\0001.sql";

    private const string SecondPartPath = CatalogPath + "\\sql\\data\\0002.sql";

    private const string LoadScriptPath = CatalogPath + "\\sql\\load.psql";

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

    [Fact]
    public void HandleStoreResult_LogsOneLineForEachScriptFile()
    {
        // Arrange
        var state = new WorkflowState();

        // Act
        _sut.HandleStoreResult(state, StoreResult(withSummary: true, scripts: Scripts(), scriptsRequested: true));

        // Assert (FR1) — a user who ran the store for the script learns where it is and what it holds,
        // without opening the folder and without the log naming a catalog file of its own.
        Messages(state).Should().ContainInOrder(
            $"        SQL schema: {SchemaScriptPath} (12 statements, 2048 bytes)",
            $"        SQL data: {DataScriptPath} (3 statements, 12 rows, 4096 bytes)");
    }

    [Fact]
    public void HandleStoreResult_LogsEveryFileOfAFamilyAndTheDriverThatIncludesThem()
    {
        // Arrange
        var state = new WorkflowState();

        // Act
        _sut.HandleStoreResult(
            state,
            StoreResult(withSummary: true, scripts: Family(), scriptsRequested: true));

        // Assert (FR10) — the split is visible as files, and the log names the one command that runs
        // them in order rather than leaving the user to guess it.
        Messages(state).Should().ContainInOrder(
            $"        SQL schema: {SchemaScriptPath} (12 statements, 2048 bytes)",
            $"        SQL data: {FirstPartPath} (3 statements, 6 rows, 4096 bytes)",
            $"        SQL data: {SecondPartPath} (2 statements, 6 rows, 2048 bytes)",
            $"        SQL load: {LoadScriptPath} (includes 2 files)");
    }

    [Fact]
    public void HandleStoreResult_DoesNotNameADriverForASingleDataFile()
    {
        // Arrange
        var state = new WorkflowState();

        // Act
        _sut.HandleStoreResult(state, StoreResult(withSummary: true, scripts: Scripts(), scriptsRequested: true));

        // Assert — one file is run by its own name; a driver line would be noise pointing at a file
        // the user does not need.
        Messages(state).Should().NotContain(message => message.Contains("SQL load:", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleStoreResult_WarnsWhenTheScriptsWereAskedForAndNotWritten()
    {
        // Arrange — the shape a refusal leaves behind: a finished catalog, no path, one warning issue.
        var state = new WorkflowState();

        // Act
        _sut.HandleStoreResult(state, StoreResult(withSummary: true, scripts: null, scriptsRequested: true));

        // Assert (FR8) — the missing file is stated as a warning beside the catalog, and the store keeps
        // its success: nothing about the catalog is incomplete.
        Messages(state).Should().Contain("SQL scripts: not written");
        state.StatusBarSummary.Should().Be("Store succeeded — 2 tables");
        state.LastError.Should().BeEmpty("a declined courtesy file is not a failure");
    }

    [Fact]
    public void HandleStoreResult_SaysNothingAboutTheScripts_WhenTheOptionWasOff()
    {
        // Arrange
        var state = new WorkflowState();

        // Act
        _sut.HandleStoreResult(state, StoreResult(withSummary: true, scripts: null, scriptsRequested: false));

        // Assert — a preference the user switched off must not read like a problem.
        Messages(state).Should().NotContain(message => message.Contains("SQL", StringComparison.Ordinal));
        state.LogMessages.Should().NotContain(entry => entry.Level == ELogLevel.Warning);
    }

    [Fact]
    public void HandleRestoreResult_DoesNotCallARestoreThatLeftTablesEmptyASuccess()
    {
        // Arrange — the shape the ordering defect leaves: the run reached its end, every object stands,
        // and 13 of the 28 tables the catalog recorded rows for hold nothing.
        var state = new WorkflowState();
        var result = RestoreResult(
            hasIssues: true,
            message: "Restore completed with issues — 13 of 28 table(s) hold a different row count",
            rowCountMismatches: 13,
            tablesChecked: 28,
            issues:
            [
                new InterchangeIssueView(
                    EInterchangeSeverity.Error,
                    "RowCountMismatch",
                    "The catalog records 462 row(s) for public.ai_assisted_decision, but the target holds 0.",
                    "table:public.ai_assisted_decision",
                    string.Empty),
            ]);

        // Act
        _sut.HandleRestoreResult(state, result);

        // Assert — the status bar and the banner state what the run completed with rather than what it
        // created, and the banner is not the green one: the object count is what a reader would otherwise
        // take for the whole story.
        state.StatusBarSummary.Should().Be("Restore completed with issues");
        state.BannerTitle.Should().Be("Restore Completed with Issues");
        state.BannerMessage.Should().Be(
            "Restore completed with issues — 13 of 28 table(s) hold a different row count");
        state.BannerSeverity.Should().Be(Wpf.Ui.Controls.InfoBarSeverity.Error);

        // The loss is its own error line, sized by the tables the check compared, beside the per-table
        // lines the issues produce.
        state.LogMessages.Should().Contain(entry =>
            entry.Level == ELogLevel.Error
            && entry.Message == "13 of 28 table(s) do not hold the row count the catalog recorded.");
    }

    [Fact]
    public void HandleRestoreResult_KeepsAGreenBannerForARestoreThatCarriedEverything()
    {
        // Arrange
        var state = new WorkflowState();

        // Act
        _sut.HandleRestoreResult(state, RestoreResult(
            hasIssues: false,
            message: "Restore succeeded: 71 objects, 3,041 rows",
            rowCountMismatches: 0,
            tablesChecked: 28,
            issues: []));

        // Assert — the plain success is unchanged, so the stronger wording stays reserved for a run that
        // actually lost something.
        state.StatusBarSummary.Should().Be("Restore succeeded — 71 objects");
        state.BannerTitle.Should().Be("Restore Complete");
        state.BannerSeverity.Should().Be(Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    /// <summary>A finished restore, as the workflow service hands it over.</summary>
    private static RestoreResult RestoreResult(
        bool hasIssues,
        string message,
        int rowCountMismatches,
        int tablesChecked,
        IReadOnlyList<InterchangeIssueView> issues) =>
        new(
            Success: true,
            Message: message,
            ObjectsApplied: 71,
            RowsRestored: 3041,
            ElapsedSeconds: 12.5,
            HasIssues: hasIssues,
            WasCancelled: false,
            WasRefused: false,
            RowCountMismatches: rowCountMismatches,
            TablesChecked: tablesChecked,
            LastCompletedChunk: null,
            Issues: issues);

    private static StoreResult StoreResult(
        bool withSummary,
        bool summaryRequested = true,
        SqlScriptPaths? scripts = null,
        bool scriptsRequested = false) =>
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
            SummaryRequested: summaryRequested,
            SqlScripts: scripts,
            SqlScriptsRequested: scriptsRequested);

    /// <summary>The pair of scripts a store with the option on reports, with their measures.</summary>
    private static SqlScriptPaths Scripts() =>
        new(
            SchemaPath: SchemaScriptPath,
            DataFiles: [new SqlScriptFile(DataScriptPath, 4096, 3, 12, null)],
            LoadPath: null,
            SchemaBytes: 2048,
            SchemaStatements: 12,
            Rows: 12,
            PayloadBytes: 0);

    /// <summary>The same scripts when the rows did not fit one file: two parts and their driver.</summary>
    private static SqlScriptPaths Family() =>
        new(
            SchemaPath: SchemaScriptPath,
            DataFiles:
            [
                new SqlScriptFile(FirstPartPath, 4096, 3, 6, "public.big"),
                new SqlScriptFile(SecondPartPath, 2048, 2, 6, null),
            ],
            LoadPath: LoadScriptPath,
            SchemaBytes: 2048,
            SchemaStatements: 12,
            Rows: 12,
            PayloadBytes: 0);

    private static IEnumerable<string> Messages(WorkflowState state) =>
        state.LogMessages.Select(entry => entry.Message);
}
