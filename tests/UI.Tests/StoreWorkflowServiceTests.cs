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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_carries_the_script_preference_into_the_interchange_request(
        bool includeSqlScripts)
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
            .ExecuteAsync(Request(includeSqlScripts: includeSqlScripts), CancellationToken.None);

        // Assert (FR13) — the checkbox reaches the product boundary as a flag and nothing else: no file
        // name, no folder name and no dialect is named on the way.
        sent.Should().NotBeNull();
        sent!.IncludeSqlScripts.Should().Be(includeSqlScripts);
    }

    [Fact]
    public async Task ExecuteAsync_returns_the_script_paths_and_records_that_they_were_asked_for()
    {
        // Arrange
        var catalogPath = @"C:\temp\testdb.catalog.rdbx";
        var scripts = new SqlScriptPaths(
            SchemaPath: Path.Combine(catalogPath, "sql", "schema.sql"),
            DataFiles: [new SqlScriptFile(Path.Combine(catalogPath, "sql", "data.sql"), 4096, 3, 12, null)],
            LoadPath: null,
            SchemaBytes: 2048,
            SchemaStatements: 12,
            Rows: 12,
            PayloadBytes: 0);
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .StoreAsync(
                Arg.Any<InterchangeStoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StoreReportWritten(catalogPath, scripts: scripts)));

        // Act
        var result = await new StoreWorkflowService(
                interchange, NullLogger<StoreWorkflowService>.Instance)
            .ExecuteAsync(Request(includeSqlScripts: true), CancellationToken.None);

        // Assert — the paths arrive measured by the product, so the handler can name the files without
        // the UI knowing what the catalog calls them (FR1, FR13).
        result.Success.Should().BeTrue();
        result.SqlScripts.Should().BeSameAs(scripts);
        result.SqlScriptsRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_with_scripts_unwritten_keeps_the_store_successful()
    {
        // Arrange — the shape a refusal leaves: a finished catalog, a null path, one warning issue.
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .StoreAsync(
                Arg.Any<InterchangeStoreRequest>(),
                Arg.Any<IProgress<InterchangeProgressView>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StoreReportWritten(scripts: null)));

        // Act
        var result = await new StoreWorkflowService(
                interchange, NullLogger<StoreWorkflowService>.Instance)
            .ExecuteAsync(Request(includeSqlScripts: true), CancellationToken.None);

        // Assert (FR8) — the user is never told "catalog not usable" about a catalog that is usable.
        result.Success.Should().BeTrue();
        result.SqlScripts.Should().BeNull();
        result.SqlScriptsRequested.Should().BeTrue("the log must be able to tell a missing file from an unasked one");
        result.Message.Should().Contain("Store succeeded").And.NotContain("not usable");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public async Task ExecuteAsync_carries_the_script_size_preference_into_the_interchange_request(
        int sqlScriptsMaxFileMb)
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
                Request(includeSqlScripts: true, sqlScriptsMaxFileMb: sqlScriptsMaxFileMb),
                CancellationToken.None);

        // Assert (D11) — the size the user picked reaches the boundary in the unit the user picked it
        // in; the single conversion to bytes belongs to the service on the other side.
        sent.Should().NotBeNull();
        sent!.SqlScriptsMaxFileMb.Should().Be(sqlScriptsMaxFileMb);
    }

    [Fact]
    public async Task ExecuteAsync_defaults_the_script_size_to_the_preferences_own()
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
                new StoreRequest(
                    new DbClone.Application.DTOs.ConnectionInfo(
                        "test.example.com", 5432, "testdb", "test_user", "fake-password", "Prefer"),
                    @"C:\temp\testdb.catalog.rdbx",
                    ChunkBytes: 32 * 1024 * 1024,
                    RowsPerChunk: 100_000,
                    IncludeData: true,
                    SnapshotConsistency: "snapshot",
                    UnsupportedFeaturePolicy: "warn",
                    TableSelection: TableSelectionSpec.All),
                CancellationToken.None);

        // Assert — a caller that says nothing gets the split the settings screen shows.
        sent!.SqlScriptsMaxFileMb.Should().Be(32);
    }

    [Fact]
    public async Task ExecuteAsync_hands_the_run_s_progress_sink_to_the_interchange_service()
    {
        // Arrange — the progress card can only move if the reports reach the sink it listens to.
        IProgress<InterchangeProgressView>? forwarded = null;
        var interchange = Substitute.For<IInterchangeService>();
        interchange
            .StoreAsync(
                Arg.Any<InterchangeStoreRequest>(),
                Arg.Do<IProgress<InterchangeProgressView>?>(p => forwarded = p),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StoreReportWritten()));

        var progress = new Progress<InterchangeProgressView>();

        // Act
        await new StoreWorkflowService(
                interchange, NullLogger<StoreWorkflowService>.Instance)
            .ExecuteAsync(Request() with { Progress = progress }, CancellationToken.None);

        // Assert — the same sink, not a null and not a fresh one nobody listens to.
        forwarded.Should().BeSameAs(progress);
    }

    /// <summary>A completed store of a small catalog, with or without the report's path.</summary>
    private static InterchangeStoreResult StoreReportWritten(
        string? catalogPath = null,
        string? summaryPath = @"C:\temp\testdb.catalog.rdbx\summary.md",
        SqlScriptPaths? scripts = null) =>
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
            SummaryPath: summaryPath,
            SqlScripts: scripts);

    private static StoreRequest Request(
        bool includeSummaryReport = true,
        bool includeSqlScripts = false,
        int sqlScriptsMaxFileMb = 32) =>
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
            IncludeSummaryReport: includeSummaryReport,
            IncludeSqlScripts: includeSqlScripts,
            SqlScriptsMaxFileMb: sqlScriptsMaxFileMb);
}
