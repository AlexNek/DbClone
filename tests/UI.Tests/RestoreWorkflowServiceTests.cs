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
}
