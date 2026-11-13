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
            .DescribeRestoreRefusalAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
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
            TablesChecked: 1,
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

    [Fact]
    public async Task ExecuteAsync_states_the_tables_that_lost_their_rows_instead_of_the_object_count()
    {
        // Arrange — the shape the ordering defect leaves behind: every object was created, and 13 of the
        // 28 tables the catalog recorded rows for came back empty. The object count reads like a success.
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .RestoreAsync(
                Arg.Any<InterchangeRestoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Restored() with
            {
                Outcome = ERestoreOutcome.CompletedWithIssues,
                ObjectsApplied = 71,
                RowsRestored = 0,
                RowCountMismatches = 13,
                TablesChecked = 28,
            }));

        // Act
        var result = await Service(interchange).ExecuteAsync(Request(), CancellationToken.None);

        // Assert — the sentence a reader sees first is the loss, sized by what the check compared, and
        // never a count of objects that were created anyway.
        result.HasIssues.Should().BeTrue();
        result.Message.Should().Be(
            "Restore completed with issues — 13 of 28 table(s) hold a different row count");
        result.TablesChecked.Should().Be(28);
    }

    /// <summary>A restore that stopped, carrying the reasons it stopped on.</summary>
    private static InterchangeRestoreResult Failed(params string[] reasons) =>
        new(
            ERestoreOutcome.Failed,
            ObjectsApplied: 0,
            ObjectsSkipped: 0,
            RowsRestored: 0,
            RowCountMismatches: 0,
            TablesChecked: 0,
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

    /// <summary>
    /// Asks the restore's own check about a target that holds nothing the old destination check counted
    /// — no table, no view, only an extension a previous restore installed — and offers the choice.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_asks_about_a_target_an_extension_alone_would_refuse()
    {
        // Arrange — the shape that dead-ended an overwrite: the database is empty by every relation
        // count, so the run went straight to the import, and the import refused it as not empty.
        const string refusal = "The target database \"testdb\" already holds 1 extension.";
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .RestoreAsync(
                Arg.Any<InterchangeRestoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Restored()));

        var confirmation = Substitute.For<IConfirmationService>();
        string? body = null;
        confirmation
            .ConfirmRestoreTargetAsync(Arg.Any<string>(), Arg.Do<string>(message => body = message))
            .Returns(Task.FromResult(ERestoreTargetChoice.Cancel));

        // Act
        var result = await Service(interchange, confirmation, refusal: refusal)
            .ExecuteAsync(Request(), CancellationToken.None);

        // Assert — the question is asked, and it names what the target holds rather than "user objects".
        body.Should().Contain("1 extension");
        result.WasCancelled.Should().BeTrue();
    }

    /// <summary>
    /// States what survived a clean that reported success instead of letting the import refuse the
    /// target the user had just asked to overwrite.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_states_what_survived_the_clean_instead_of_starting_the_restore()
    {
        // Arrange — a clean whose one step it could not drop was logged as a warning, so it finished
        // saying the target was cleaned while a subscription the restore counts was still there.
        const string refusal = "The target database \"testdb\" already holds 1 subscription.";
        var interchange = Substitute.For<IInterchangeService>();

        var confirmation = Substitute.For<IConfirmationService>();
        confirmation
            .ConfirmRestoreTargetAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(ERestoreTargetChoice.UpdateSelected));

        var catalog = Substitute.For<ITableCatalogService>();
        catalog
            .CleanTargetDatabaseAsync(
                Arg.Any<ConnectionInfo>(),
                Arg.Any<Action<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        var result = await Service(interchange, confirmation, catalog, refusal)
            .ExecuteAsync(Request(), CancellationToken.None);

        // Assert — the sentence names what is still there, and nothing was restored over it.
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("1 subscription").And.Contain("No restore was started.");
        await interchange.DidNotReceive().RestoreAsync(
            Arg.Any<InterchangeRestoreRequest>(),
            Arg.Any<IProgress<InterchangeProgressView>?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// States the reason the product reported for a refusal. The outcome used to be a fixed sentence —
    /// "Restore refused — target database not empty" — which told the user nothing about what to remove.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_states_the_reason_a_refused_restore_reported()
    {
        // Arrange
        const string reason = "The target database \"testdb\" already holds 1 extension.";
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .RestoreAsync(
                Arg.Any<InterchangeRestoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Refused(reason)));

        // Act
        var result = await Service(interchange).ExecuteAsync(Request(), CancellationToken.None);

        // Assert
        result.WasRefused.Should().BeTrue();
        result.Message.Should().Be($"Restore refused: {reason}");
    }

    /// <summary>A restore the product refused, carrying the sentence it refused with.</summary>
    private static InterchangeRestoreResult Refused(string reason) =>
        new(
            ERestoreOutcome.Refused,
            ObjectsApplied: 0,
            ObjectsSkipped: 0,
            RowsRestored: 0,
            RowCountMismatches: 0,
            TablesChecked: 0,
            Issues:
            [
                new InterchangeIssueView(
                    EInterchangeSeverity.Warning,
                    "RestoreRefused",
                    reason,
                    string.Empty,
                    "A restore applies its catalog to an empty database; "
                    + "cleaning the target is a separate act performed beforehand."),
            ],
            Validation: null,
            LastCompletedChunk: null,
            Elapsed: TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// The workflow over a target the restore's own check answers for: <paramref name="refusal"/> is
    /// the sentence it gives, or <see langword="null"/> for a target it accepts.
    /// </summary>
    private static RestoreWorkflowService Service(
        IInterchangeService interchange,
        IConfirmationService? confirmation = null,
        ITableCatalogService? catalog = null,
        string? refusal = null)
    {
        interchange
            .DescribeRestoreRefusalAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(refusal));

        var serverAdmin = Substitute.For<IServerAdminService>();
        serverAdmin
            .TestConnectionAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("test-version"));

        return new RestoreWorkflowService(
            interchange,
            catalog ?? Substitute.For<ITableCatalogService>(),
            confirmation ?? Substitute.For<IConfirmationService>(),
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
