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
            PackedZipPath: null,
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
            PackAfterStore: false,
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
}
