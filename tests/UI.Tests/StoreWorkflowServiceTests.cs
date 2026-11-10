using DbClone.Application.Interchange;
using DbClone.Application.TableFilter;
using DbClone.UI.StoreRestore.Workflows;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace DbClone.UI.Tests;

/// <summary>
/// Verifies that store does not invoke the separate blob extraction surface.
/// </summary>
public sealed class StoreWorkflowServiceTests
{
    [Fact]
    public async Task ExecuteAsync_does_not_create_or_request_catalog_root_blob_extraction()
    {
        var interchange = Substitute.For<IInterchangeService>();
        var catalogPath = Path.Combine(Path.GetTempPath(), "DbCloneStoreWorkflowTest_" + Guid.NewGuid().ToString("N"));
        var storeResult = new InterchangeStoreResult(
            EStoreOutcome.Success,
            SectionsWritten: 1,
            TablesExported: 1,
            RowsExported: 1,
            BytesExported: 100,
            ChunksWritten: 1,
            IsComplete: true,
            ConsistencyModeUsed: "snapshot",
            TablesThatFellBackToSourceOrder: [],
            VerificationPassed: true,
            Issues: [],
            CatalogPath: catalogPath,
            Elapsed: TimeSpan.Zero);
        interchange.StoreAsync(
                Arg.Any<InterchangeStoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(storeResult));

        var service = new StoreWorkflowService(
            interchange,
            NullLogger<StoreWorkflowService>.Instance);
        var request = new StoreRequest(
            new DbClone.Application.DTOs.ConnectionInfo(
                "test.example.com",
                5432,
                "testdb",
                "test_user",
                "fake-password",
                "Prefer"),
            catalogPath,
            ChunkBytes: 32 * 1024 * 1024,
            RowsPerChunk: 100_000,
            IncludeData: true,
            SnapshotConsistency: "snapshot",
            UnsupportedFeaturePolicy: "warn",
            TableSelection: TableSelectionSpec.All);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        await interchange.DidNotReceive().ExtractAllBlobsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IProgress<BlobExtractionProgressView>?>(),
            Arg.Any<CancellationToken>());
        Directory.Exists(Path.Combine(catalogPath, "blobs")).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_carries_the_summary_preference_into_the_interchange_request(
        bool includeSummaryReport)
    {
        // Arrange
        InterchangeStoreRequest? sent = null;
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .StoreAsync(
                Arg.Any<InterchangeStoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.Arg<InterchangeStoreRequest>();

                return Task.FromResult(StoreReportWritten());
            });

        // Act
        await new StoreWorkflowService(
                interchange, NullLogger<StoreWorkflowService>.Instance)
            .ExecuteAsync(
                Request(includeSummaryReport),
                CancellationToken.None);

        // Assert (FR11) — the setting reaches the service that talks to the product; the workflow is the
        // only translation point between the app's option name and the catalog's.
        sent.Should().NotBeNull();
        sent!.IncludeSummaryReport.Should().Be(includeSummaryReport);
    }

    [Fact]
    public async Task ExecuteAsync_returns_the_summary_path_and_records_that_it_was_asked_for()
    {
        // Arrange
        var catalogPath = @"C:\temp\testdb.catalog.rdbx";
        var summaryPath = Path.Combine(catalogPath, "summary.md");
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .StoreAsync(
                Arg.Any<InterchangeStoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StoreReportWritten(catalogPath, summaryPath)));

        // Act
        var result = await new StoreWorkflowService(
                interchange, NullLogger<StoreWorkflowService>.Instance)
            .ExecuteAsync(Request(), CancellationToken.None);

        // Assert (FR12) — both facts the log needs: where the file is, and that the user wanted one.
        result.SummaryPath.Should().Be(summaryPath);
        result.SummaryRequested.Should().BeTrue();
        result.Success.Should().BeTrue();
        result.Message.Should().EndWith(", summary written");
    }

    [Fact]
    public async Task ExecuteAsync_with_no_summary_written_while_it_was_asked_for_keeps_the_store_usable()
    {
        // Arrange — the exact shape D4.2 hands back: a finished catalog, a report stage that stopped.
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .StoreAsync(
                Arg.Any<InterchangeStoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StoreReportWritten(summaryPath: null)));

        // Act
        var result = await new StoreWorkflowService(
                interchange, NullLogger<StoreWorkflowService>.Instance)
            .ExecuteAsync(Request(), CancellationToken.None);

        // Assert — the user is never told "catalog not usable" about a catalog that is usable, and the
        // missing path is visible to the log as a null rather than as a guess.
        result.Success.Should().BeTrue();
        result.SummaryPath.Should().BeNull();
        result.SummaryRequested.Should().BeTrue();
        result.Message.Should().Contain("Store succeeded").And.NotContain("not usable");
    }

    [Fact]
    public async Task ExecuteAsync_with_the_option_off_does_not_claim_a_summary_was_written()
    {
        // Arrange
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .StoreAsync(
                Arg.Any<InterchangeStoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StoreReportWritten(summaryPath: null)));

        // Act
        var result = await new StoreWorkflowService(
                interchange, NullLogger<StoreWorkflowService>.Instance)
            .ExecuteAsync(Request(includeSummaryReport: false), CancellationToken.None);

        // Assert
        result.Message.Should().NotContain("summary");
        result.SummaryRequested.Should().BeFalse("the log must stay silent about a file nobody asked for");
    }

    /// <summary>A completed store of a small catalog, with or without the report's path.</summary>
    private static InterchangeStoreResult StoreReportWritten(
        string? catalogPath = null, string? summaryPath = @"C:\temp\testdb.catalog.rdbx\summary.md") =>
        new(
            EStoreOutcome.Success,
            SectionsWritten: 8,
            TablesExported: 2,
            RowsExported: 12,
            BytesExported: 4096,
            ChunksWritten: 2,
            IsComplete: true,
            ConsistencyModeUsed: "snapshot",
            TablesThatFellBackToSourceOrder: [],
            VerificationPassed: true,
            Issues: [],
            CatalogPath: catalogPath ?? @"C:\temp\testdb.catalog.rdbx",
            Elapsed: TimeSpan.FromSeconds(1),
            SummaryPath: summaryPath);

    private static StoreRequest Request(bool includeSummaryReport = true) =>
        new(
            new DbClone.Application.DTOs.ConnectionInfo(
                "test.example.com", 5432, "testdb", "test_user", "fake-password", "Prefer"),
            @"C:\temp\testdb.catalog.rdbx",
            ChunkBytes: 32 * 1024 * 1024,
            RowsPerChunk: 100_000,
            IncludeData: true,
            SnapshotConsistency: "snapshot",
            UnsupportedFeaturePolicy: "warn",
            TableSelection: TableSelectionSpec.All,
            IncludeSummaryReport: includeSummaryReport);
}
