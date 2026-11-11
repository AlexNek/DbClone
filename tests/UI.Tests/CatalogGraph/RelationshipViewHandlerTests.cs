using System.IO;

using CanonicalModel.Enums;
using DbClone.Application.Interchange;
using DbClone.UI.Services.GraphLayout;
using DbClone.UI.StoreRestore.Handlers;
using DbClone.UI.ViewModels;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// T8: the workflow writes one document into the catalog — named after it, and nothing beside it — and
/// a write that cannot land leaves neither a partial document nor a temporary file behind.
/// </summary>
/// <remarks>
/// The stages are the real ones, so the file that lands is the document the app actually produces. Only
/// the catalog read and the shell open are replaced: one is the product's boundary, the other is the
/// operating system's.
/// </remarks>
public sealed class RelationshipViewHandlerTests : IDisposable
{
    private const string CatalogFolderName = "Invoice_matcher_2026-11-10_16-10";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dbclone-relationship-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingHtmlFileOpener _opener = new();

    public RelationshipViewHandlerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder the runner could not remove is not a test failure.
        }
    }

    [Fact]
    public async Task ShowAsync_WritesTheDocumentIntoTheCatalog_AndOpensIt()
    {
        // Arrange
        var catalog = CreateCatalog();
        var sut = CreateHandler(interchange => Reading(interchange, View()));

        // Act
        await sut.ShowAsync(catalog, new WorkflowState());

        // Assert (D5) — the catalog folder, the catalog's own name, one document, nothing beside it.
        var picture = PictureIn(catalog);
        File.Exists(picture).Should().BeTrue();
        _opener.Opened.Should().ContainSingle().Which.Should().Be(picture);
        File.ReadAllText(picture).Should().StartWith("<!DOCTYPE html>").And.EndWith("</html>\n");
        SiblingsBesideTheCatalog().Should().BeEmpty("the store folder holds the picture, not its parent");
    }

    [Fact]
    public async Task ShowAsync_AddsOnlyThePictureToTheCatalogFolder()
    {
        // Arrange — the picture is the one file the app contributes; the archive's own files stay as the
        // store wrote them.
        var catalog = CreateCatalog();
        var before = FolderEntries(catalog);
        var sut = CreateHandler(interchange => Reading(interchange, View()));

        // Act
        await sut.ShowAsync(catalog, new WorkflowState());

        // Assert — the catalog's files plus the picture, and nothing unfinished anywhere.
        FolderEntries(catalog).Should().BeEquivalentTo([.. before, Path.GetFileName(PictureIn(catalog))]);
        TempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ShowAsync_ReportsTheFailureAndLeavesNoPartialFile_WhenTheDocumentCannotLand()
    {
        // Arrange — a folder occupies the document's own name, so the move cannot land.
        var catalog = CreateCatalog();
        Directory.CreateDirectory(PictureIn(catalog));
        var state = new WorkflowState();
        var sut = CreateHandler(interchange => Reading(interchange, View()));

        // Act
        await sut.ShowAsync(catalog, state);

        // Assert — a stated failure, no leftover temporary file, and the shell was never asked.
        state.LastError.Should().StartWith("Relationship view failed:");
        TempFiles().Should().BeEmpty();
        _opener.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task ShowAsync_KeepsTheCompleteDocument_WhenTheShellRefusesToOpenIt()
    {
        // Arrange — the file is written before anything tries to open it.
        var openingFails = new RecordingHtmlFileOpener { Failure = new InvalidOperationException("no handler") };
        var catalog = CreateCatalog();
        var state = new WorkflowState();
        var sut = CreateHandler(interchange => Reading(interchange, View()), openingFails);

        // Act
        await sut.ShowAsync(catalog, state);

        // Assert — a shortcut that failed is a warning that names the file, not a failed run.
        File.ReadAllText(PictureIn(catalog))
            .Should().EndWith("</html>\n");
        state.LastError.Should().BeEmpty();
        state.LogMessages.Should().ContainSingle(entry => entry.Level == ELogLevel.Warning)
            .Which.Message.Should().Contain(CatalogFolderName + ".html");
    }

    [Fact]
    public async Task ShowAsync_ReportsAReadFailure_AndWritesNothing()
    {
        // Arrange — a folder that is not a catalog: the product's reader refuses it.
        var catalog = CreateCatalog();
        var before = FolderEntries(catalog);
        var state = new WorkflowState();
        var sut = CreateHandler(interchange =>
            interchange.ReadGraphAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<CatalogGraphView>(new DirectoryNotFoundException("no catalog"))));

        // Act
        await sut.ShowAsync(catalog, state);

        // Assert
        state.LastError.Should().Contain("no catalog");
        FolderEntries(catalog).Should().Equal(before);
        TempFiles().Should().BeEmpty();
        _opener.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task TryWriteAsync_WritesTheDocumentIntoTheCatalog_AndOpensNothing()
    {
        // Arrange — what a finished store asks for (FR17): the file, not the browser.
        var catalog = CreateCatalog();
        var state = new WorkflowState();
        var sut = CreateHandler(interchange => Reading(interchange, View()));

        // Act
        var written = await sut.TryWriteAsync(catalog, state);

        // Assert — the same document the view produces, and the shell was never asked.
        var picture = PictureIn(catalog);
        written.Should().Be(picture);
        File.ReadAllText(picture).Should().StartWith("<!DOCTYPE html>").And.EndWith("</html>\n");
        _opener.Opened.Should().BeEmpty();
        state.LastError.Should().BeEmpty();
        TempFiles().Should().BeEmpty();
        SiblingsBesideTheCatalog().Should().BeEmpty();
    }

    [Fact]
    public async Task TryWriteAsync_ReportsAWriteThatCannotLandAsAWarning_AndLeavesTheRunAsItWas()
    {
        // Arrange — the same obstruction as above: a folder sits where the document belongs.
        var catalog = CreateCatalog();
        Directory.CreateDirectory(PictureIn(catalog));
        var state = new WorkflowState();
        var sut = CreateHandler(interchange => Reading(interchange, View()));

        // Act
        var written = await sut.TryWriteAsync(catalog, state);

        // Assert — the catalog is complete, so an absent picture is a warning and never a failed store.
        written.Should().BeNull();
        state.LastError.Should().BeEmpty();
        state.LogMessages.Should().ContainSingle(entry => entry.Level == ELogLevel.Warning)
            .Which.Message.Should().StartWith("Relationship view could not be written:");
        TempFiles().Should().BeEmpty();
        _opener.Opened.Should().BeEmpty();
    }

    /// <summary>A catalog folder holding the two files a reader would find, and nothing else.</summary>
    private string CreateCatalog()
    {
        var catalog = Path.Combine(_root, CatalogFolderName);
        Directory.CreateDirectory(catalog);
        File.WriteAllText(Path.Combine(catalog, "catalog.json"), "{}");
        File.WriteAllText(Path.Combine(catalog, "manifest.json"), "{}");

        return catalog;
    }

    private RelationshipViewHandler CreateHandler(
        Action<IInterchangeService> prepare,
        RecordingHtmlFileOpener? opener = null)
    {
        var interchange = Substitute.For<IInterchangeService>();
        prepare(interchange);

        return new RelationshipViewHandler(
            interchange,
            new CatalogGraphAnalyzer(),
            new GraphLayoutEngine(),
            new CatalogGraphHtmlRenderer(),
            opener ?? _opener,
            NullLogger<RelationshipViewHandler>.Instance);
    }

    private static void Reading(IInterchangeService interchange, CatalogGraphView view) =>
        interchange.ReadGraphAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(view));

    private static CatalogGraphView View() =>
        CatalogGraphFixture.Create()
            .Table("public", "customers", rowCount: 3, Column("id", "uuid", primaryKey: true))
            .Table("public", "orders", rowCount: 5, Column("id", "uuid", primaryKey: true),
                Column("customer_id", "uuid", referencing: true))
            .Edge(
                CatalogGraphFixture.TableId("public", "orders"),
                CatalogGraphFixture.TableId("public", "customers"),
                "orders_customer_fkey",
                ["customer_id"],
                ["id"],
                mandatory: true,
                onDelete: "cascade")
            .Build();

    private static CatalogColumnView Column(
        string name,
        string logicalType,
        bool primaryKey = false,
        bool referencing = false) =>
        new(name, logicalType, IsNullable: false, primaryKey, referencing, IsReferencedByForeignKey: false, Comment: null);

    /// <summary>The document the path rule derives for a catalog: inside it, named after it (D5).</summary>
    private static string PictureIn(string catalog) =>
        Path.Combine(catalog, Path.GetFileName(catalog) + ".html");

    /// <summary>Any document sitting in the store folder rather than in the catalog it describes.</summary>
    private IEnumerable<string> SiblingsBesideTheCatalog() => Directory.GetFiles(_root, "*.html");

    private static string[] FolderEntries(string folder) =>
        [.. Directory.GetFileSystemEntries(folder)
            .Select(entry => Path.GetFileName(entry)!)
            .Order(StringComparer.Ordinal)];

    private IEnumerable<string> TempFiles() =>
        Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories);

    /// <summary>An opener that records what it was asked to open, or fails the way a missing handler does.</summary>
    private sealed class RecordingHtmlFileOpener : IHtmlFileOpener
    {
        internal List<string> Opened { get; } = [];

        internal Exception? Failure { get; init; }

        public void Open(string path)
        {
            if (Failure is not null) throw Failure;

            Opened.Add(path);
        }
    }
}
