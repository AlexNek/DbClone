using DbClone.Application.Catalog;
using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbClone.Application.Interchange;
using DbClone.UI.StoreRestore.Workflows;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace DbClone.UI.Tests;

/// <summary>
/// Verifies that the restore workflow passes the run's progress sink on to the interchange
/// service. The progress card can only move if the reports reach it.
/// </summary>
public sealed class RestoreWorkflowServiceTests
{
    [Fact]
    public async Task ExecuteAsync_hands_the_run_s_progress_sink_to_the_interchange_service()
    {
        // Arrange — an empty destination, so the run goes straight to the import.
        IProgress<InterchangeProgressView>? forwarded = null;
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .RestoreAsync(
                Arg.Any<InterchangeRestoreRequest>(),
                Arg.Do<IProgress<InterchangeProgressView>?>(p => forwarded = p),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Restored()));

        var serverAdmin = Substitute.For<IServerAdminService>();
        serverAdmin
            .TestConnectionAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("test-version"));

        var catalog = Substitute.For<ITableCatalogService>();
        catalog
            .CheckDestinationHasDataAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var progress = new Progress<InterchangeProgressView>();
        var request = new RestoreRequest(
            new ConnectionInfo(
                "test.example.com", 5432, "testdb", "test_user", "fake-password", "Prefer"),
            @"C:\temp\testdb.catalog.rdbx",
            UnsupportedFeaturePolicy: "warn",
            Progress: progress);

        // Act
        await new RestoreWorkflowService(
                interchange,
                catalog,
                Substitute.For<IConfirmationService>(),
                serverAdmin,
                NullLogger<RestoreWorkflowService>.Instance)
            .ExecuteAsync(request, CancellationToken.None);

        // Assert — the same sink, not a null and not a fresh one nobody listens to.
        forwarded.Should().BeSameAs(progress);
    }

    private static InterchangeRestoreResult Restored() =>
        new(
            ERestoreOutcome.Success,
            ObjectsApplied: 4,
            ObjectsSkipped: 0,
            RowsRestored: 12,
            RowCountMismatches: 0,
            Issues: [],
            Validation: null,
            LastCompletedChunk: null,
            Elapsed: TimeSpan.FromSeconds(1));

    [Fact]
    public async Task ExecuteAsync_states_why_a_restore_failed_instead_of_counting_its_issues()
    {
        // Arrange — the shape a catalog this version cannot read leaves: no stage ran, one error issue
        // whose sentence is the whole of what happened and the only thing the user can act on.
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .RestoreAsync(
                Arg.Any<InterchangeRestoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failed(
                "This catalog was written by interchange format version 3, "
                + "which this version cannot read. Re-export the database.")));

        // Act
        var result = await Service(interchange).ExecuteAsync(Request(), CancellationToken.None);

        // Assert — "1 issue(s)" named nothing a reader could act on; the reason is the outcome.
        result.Success.Should().BeFalse();
        result.Message.Should().Be(
            "Restore failed: This catalog was written by interchange format version 3, "
            + "which this version cannot read. Re-export the database.");
    }

    [Fact]
    public async Task ExecuteAsync_names_the_first_reason_and_counts_the_rest()
    {
        // Arrange — two steps that could not be applied, the second of which is a consequence.
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .RestoreAsync(
                Arg.Any<InterchangeRestoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failed(
                "The tables stage could not be applied.",
                "The rows of 66 table(s) were not written.")));

        // Act
        var result = await Service(interchange).ExecuteAsync(Request(), CancellationToken.None);

        // Assert — one sentence for the banner, the others counted rather than dropped or stacked.
        result.Message.Should().Be(
            "Restore failed: The tables stage could not be applied. (and 1 more)");
    }

    /// <summary>A restore that stopped, carrying the reasons it stopped on.</summary>
    private static InterchangeRestoreResult Failed(params string[] reasons) =>
        new(
            ERestoreOutcome.Failed,
            ObjectsApplied: 0,
            ObjectsSkipped: 0,
            RowsRestored: 0,
            RowCountMismatches: 0,
            Issues:
            [
                .. reasons.Select(reason => new InterchangeIssueView(
                    EInterchangeSeverity.Error,
                    "RestoreStageFailed",
                    reason,
                    string.Empty,
                    string.Empty)),
            ],
            Validation: null,
            LastCompletedChunk: null,
            Elapsed: TimeSpan.FromSeconds(2));

    /// <summary>The service over an empty destination, so the run goes straight to the import.</summary>
    private static RestoreWorkflowService Service(IInterchangeService interchange)
    {
        var serverAdmin = Substitute.For<IServerAdminService>();
        serverAdmin
            .TestConnectionAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("test-version"));

        var catalog = Substitute.For<ITableCatalogService>();
        catalog
            .CheckDestinationHasDataAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        return new RestoreWorkflowService(
            interchange,
            catalog,
            Substitute.For<IConfirmationService>(),
            serverAdmin,
            NullLogger<RestoreWorkflowService>.Instance);
    }

    private static RestoreRequest Request() =>
        new(
            new ConnectionInfo(
                "test.example.com", 5432, "testdb", "test_user", "fake-password", "Prefer"),
            @"C:\temp\testdb.catalog.rdbx",
            UnsupportedFeaturePolicy: "warn");
}
