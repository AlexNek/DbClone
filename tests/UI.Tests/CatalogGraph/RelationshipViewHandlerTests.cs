using System.IO;

using CanonicalModel.Enums;
using DbClone.Application.DTOs;
using DbClone.Application.Relationship;
using DbClone.UI.Services;
using DbClone.UI.Services.GraphLayout;
using DbClone.UI.Services.Relationship;
using DbClone.UI.StoreRestore.Handlers;
using DbClone.UI.StoreRestore.Services;
using DbClone.UI.ViewModels;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Feature 11: the workflow reads an origin, builds one document and hands it to whichever presenter the
/// app holds — a view writes nothing (D1). The one file the product still writes is the picture a store
/// puts inside its catalog, and a write that cannot land leaves neither a partial file nor a temporary
/// one (FR17).
/// </summary>
/// <remarks>
/// The reader is the application's boundary and the presenter is the shell's, so both are replaced; the
/// builder and the picture writer are the real ones, so the document asserted here is the one the app
/// produces.
/// </remarks>
public sealed class RelationshipViewHandlerTests : IDisposable
{
    private const string CatalogFolderName = "Invoice_matcher_2026-11-10_16-10";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dbclone-relationship-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingPresenter _presenter = new();

    private readonly RecordingCursor _cursor = new();

    private readonly IRelationshipGraphService _graph = Substitute.For<IRelationshipGraphService>();

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
    public async Task ShowCatalogAsync_PresentsTheDocument_AndWritesNothing()
    {
        // Arrange — a stored catalog, handed over the way the catalog reader hands it over.
        var catalog = CreateCatalog();
        var before = Snapshot(catalog);
        Reading(View());
        var sut = CreateHandler();

        // Act
        await sut.ShowCatalogAsync(catalog, new WorkflowState());

        // Assert — the document reaches the presenter and the folder is untouched: a view writes nothing.
        // One document arrives: the reader is shown an arrangement once, not a plain one that is then
        // replaced in front of them.
        _presenter.Presented.Should().ContainSingle();
        _presenter.Presented.Should().OnlyContain(document => document.Html.StartsWith("<!DOCTYPE html>"));
        _presenter.Presented[^1].Origin.Label.Should().Be("fixture");
        Snapshot(catalog).Should().BeEquivalentTo(before);
        TempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ShowDatabaseAsync_ReadsTheSideThatWasAsked_AndCreatesNoFileAtAll()
    {
        // Arrange — no store, no folder: the side names a database and nothing else (criteria 1–3).
        var catalog = CreateCatalog();
        var before = Snapshot(_root);
        _graph.ReadDatabaseAsync(
                Arg.Any<ConnectionInfo>(),
                Arg.Any<ERelationshipRole>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(View()));
        var sut = CreateHandler();

        // Act
        await sut.ShowDatabaseAsync(Connection(), ERelationshipRole.Target, new WorkflowState());

        // Assert — the target side was read, and nothing anywhere on disk changed.
        await _graph.Received(1).ReadDatabaseAsync(
            Arg.Is<ConnectionInfo>(info => info != null && info.DatabaseName == "testdb"),
            ERelationshipRole.Target,
            Arg.Any<CancellationToken>());
        _presenter.Presented.Should().NotBeEmpty();
        Snapshot(_root).Should().BeEquivalentTo(before);
        TempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ShowDatabaseAsync_OfADatabaseWithNoTables_WarnsAndStillPresents()
    {
        // Arrange — an empty database is a fact the picture can state, so it is drawn and named.
        _graph.ReadDatabaseAsync(
                Arg.Any<ConnectionInfo>(),
                Arg.Any<ERelationshipRole>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyDatabase()));
        var state = new WorkflowState();
        var sut = CreateHandler();

        // Act
        await sut.ShowDatabaseAsync(Connection(), ERelationshipRole.Source, state);

        // Assert
        state.LogMessages.Should().ContainSingle(entry => entry.Level == ELogLevel.Warning)
            .Which.Message.Should().Contain("testdb");
        _presenter.Presented.Should().NotBeEmpty("a database with no tables is still a document");
    }

    [Fact]
    public async Task ShowCatalogAsync_OfAFolderThatIsNotACatalog_ReportsTheError_AndPresentsNothing()
    {
        // Arrange — a folder that names no catalog: the product's reader refuses it.
        var catalog = CreateCatalog();
        var before = Snapshot(catalog);
        _graph.ReadCatalogAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RelationshipGraphView>(new DirectoryNotFoundException("no catalog")));
        var state = new WorkflowState();
        var sut = CreateHandler();

        // Act
        await sut.ShowCatalogAsync(catalog, state);

        // Assert — a failed read is an error that names the reason, and nothing is presented.
        state.LastError.Should().Contain("no catalog");
        _presenter.Presented.Should().BeEmpty();
        Snapshot(catalog).Should().BeEquivalentTo(before);
        TempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ShowCatalogAsync_WhenTheDocumentCannotBeShown_IsAWarningThatNamesTheOrigin()
    {
        // Arrange — the document is built; only showing it fails, which costs the reader the window.
        var catalog = CreateCatalog();
        Reading(View());
        var state = new WorkflowState();
        var refusing = new RecordingPresenter { Failure = new InvalidOperationException("no viewer") };
        var sut = CreateHandler(refusing);

        // Act
        await sut.ShowCatalogAsync(catalog, state);

        // Assert
        state.LastError.Should().BeEmpty("the view was read and rendered; presenting it is what failed");
        state.LogMessages.Should().ContainSingle(entry => entry.Level == ELogLevel.Warning)
            .Which.Message.Should().Contain("fixture").And.Contain("no viewer");
    }

    [Fact]
    public async Task The_waiting_pointer_is_the_answer_only_for_a_presenter_with_no_window()
    {
        // Arrange — the browser has no page to state the work in before the document exists, so the
        // pointer is what tells the reader the request was heard (D7).
        var catalog = CreateCatalog();
        Reading(View());
        var sut = CreateHandler(new RecordingPresenter { AcknowledgesNotice = false });

        // Act
        await sut.ShowCatalogAsync(catalog, new WorkflowState());

        // Assert — shown for the whole run, and put back once the document is on screen.
        _cursor.Calls.Should().Equal(nameof(IWaitCursor.Show), nameof(IWaitCursor.Hide));

        // Act — a run that cannot read its origin ends no differently for the pointer.
        _graph.ReadCatalogAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RelationshipGraphView>(new DirectoryNotFoundException("no catalog")));
        await sut.ShowCatalogAsync(Path.Combine(_root, CatalogFolderName), new WorkflowState());

        // Assert
        _cursor.Calls.Should().Equal(
            nameof(IWaitCursor.Show), nameof(IWaitCursor.Hide),
            nameof(IWaitCursor.Show), nameof(IWaitCursor.Hide));
    }

    [Fact]
    public async Task The_pointer_is_not_raised_when_the_window_already_states_the_work()
    {
        // Arrange — an in-app viewer opens with the notice, so a pointer over the main window would
        // say nothing the reader is not already looking at.
        var catalog = CreateCatalog();
        Reading(View());
        var sut = CreateHandler();

        // Act
        await sut.ShowCatalogAsync(catalog, new WorkflowState());

        // Assert
        _presenter.Notices.Should().HaveCount(2, "one notice for the read, one for the arrangement");
        _cursor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_viewer_states_the_origin_before_that_origin_is_read()
    {
        // Arrange — the read is the long part of the request, so the window names what it is reading
        // before the first fact has arrived.
        var catalog = CreateCatalog();
        var noticesWhenRead = -1;
        _graph.ReadCatalogAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                noticesWhenRead = _presenter.Notices.Count;

                return Task.FromResult(View());
            });
        var sut = CreateHandler();

        // Act
        await sut.ShowCatalogAsync(catalog, new WorkflowState());

        // Assert
        noticesWhenRead.Should().Be(1, "the notice is the window's first page, not its last");
        _presenter.Notices[0].Origin.Should().Be(CatalogFolderName);
        _presenter.Notices[0].Headline.Should().Contain("Reading");
        _presenter.Notices[0].IsFailure.Should().BeFalse();
    }

    [Fact]
    public async Task The_window_states_the_arrangement_once_the_origin_is_read()
    {
        // Arrange — reading and arranging are two waits: once the read answers, the window stops
        // claiming the origin is still being read.
        var catalog = CreateCatalog();
        Reading(View());
        var sut = CreateHandler();

        // Act
        await sut.ShowCatalogAsync(catalog, new WorkflowState());

        // Assert
        _presenter.Notices.Should().HaveCount(2);
        _presenter.Notices[1].Origin.Should().Be(CatalogFolderName);
        _presenter.Notices[1].Headline.Should().Contain("Arranging");
        _presenter.Notices[1].IsFailure.Should().BeFalse();
    }

    [Fact]
    public async Task A_side_is_named_in_the_notice_so_two_panels_cannot_be_confused()
    {
        // Arrange — the same database asked from both panels states which side is being read.
        _graph.ReadDatabaseAsync(Arg.Any<ConnectionInfo>(), Arg.Any<ERelationshipRole>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(View()));
        var sut = CreateHandler();

        // Act
        await sut.ShowDatabaseAsync(Connection(), ERelationshipRole.Source, new WorkflowState());

        // Assert
        _presenter.Notices[0].Origin.Should().Be("testdb (source)");
    }

    [Fact]
    public async Task A_read_that_fails_is_stated_in_the_window_that_was_opened_for_it()
    {
        // Arrange — the failure the reader reported was invisible: the window is already open, so the
        // reason it stopped belongs in it rather than in a state the banner may not show.
        var catalog = CreateCatalog();
        _graph.ReadCatalogAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RelationshipGraphView>(new DirectoryNotFoundException("no catalog")));
        var state = new WorkflowState();
        var sut = CreateHandler();

        // Act
        await sut.ShowCatalogAsync(catalog, state);

        // Assert
        state.LastError.Should().Contain("no catalog");
        _presenter.Presented.Should().BeEmpty("there is no picture to present");
        _presenter.Notices.Should().HaveCount(2, "one for the work, one for the reason it stopped");
        _presenter.Notices[^1].IsFailure.Should().BeTrue();
        _presenter.Notices[^1].Detail.Should().Be("no catalog");
    }

    [Fact]
    public async Task The_refresh_a_presenter_is_given_ReadsTheOriginAgain()
    {
        // Arrange — a window offers Refresh; it re-runs the delegate the handler supplied (D6).
        var catalog = CreateCatalog();
        Reading(View());
        var sut = CreateHandler();

        // Act
        await sut.ShowCatalogAsync(catalog, new WorkflowState());
        _presenter.Refresh.Should().NotBeNull();
        var again = await _presenter.Refresh!(CancellationToken.None);

        // Assert
        again.Origin.Label.Should().Be("fixture");
        await _graph.Received(2).ReadCatalogAsync(catalog, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_redraw_a_presenter_is_given_DrawsTheSameOriginForAnotherSelection()
    {
        // Arrange — the window offers the chips; a selection is drawn again without reading anything,
        // and the tables it hides take no room in the arrangement (T28).
        var catalog = CreateCatalog();
        Reading(TwoSchemas());
        var sut = CreateHandler();

        // Act
        await sut.ShowCatalogAsync(catalog, new WorkflowState());
        _presenter.Redraw.Should().NotBeNull();
        var stagingOff = _presenter.Redraw!(new HashSet<int> { 1 });

        // Assert — the bar's second schema chip is the one switched off, so the picture holds only the
        // schema that is on — while the count line still states the catalog's own totals.
        stagingOff.Html.Should().Contain(">orders</text>").And.NotContain("imports");
        stagingOff.Html.Should().Contain("\"tables\":3").And.Contain("aria-pressed=\"false\">staging</button>");
        await _graph.Received(1).ReadCatalogAsync(catalog, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryWriteAsync_PutsThePictureInsideTheCatalog_AndPresentsNothing()
    {
        // Arrange — what a finished store asks for (FR17): the file, not a window.
        var catalog = CreateCatalog();
        var state = new WorkflowState();
        Reading(View());
        var sut = CreateHandler();

        // Act
        var written = await sut.TryWriteAsync(catalog, state);

        // Assert — the catalog folder, the catalog's own name, one document, nothing beside it.
        var picture = PictureIn(catalog);
        written.Should().Be(picture);
        File.ReadAllText(picture).Should().StartWith("<!DOCTYPE html>").And.EndWith("</html>\n");
        _presenter.Presented.Should().BeEmpty();
        state.LastError.Should().BeEmpty();
        TempFiles().Should().BeEmpty();
        SiblingsBesideTheCatalog().Should().BeEmpty("the store folder holds the picture, not its parent");
    }

    [Fact]
    public async Task TryWriteAsync_AddsOnlyThePictureToTheCatalogFolder()
    {
        // Arrange — the picture is the one file the app contributes; the archive's own files stay as the
        // store wrote them.
        var catalog = CreateCatalog();
        var before = FolderEntries(catalog);
        Reading(View());
        var sut = CreateHandler();

        // Act
        await sut.TryWriteAsync(catalog, new WorkflowState());

        // Assert
        FolderEntries(catalog).Should().BeEquivalentTo([.. before, Path.GetFileName(PictureIn(catalog))]);
        TempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task TryWriteAsync_ReportsAWriteThatCannotLandAsAWarning_AndLeavesTheRunAsItWas()
    {
        // Arrange — a folder occupies the document's own name, so the move cannot land.
        var catalog = CreateCatalog();
        Directory.CreateDirectory(PictureIn(catalog));
        var state = new WorkflowState();
        Reading(View());
        var sut = CreateHandler();

        // Act
        var written = await sut.TryWriteAsync(catalog, state);

        // Assert — the catalog is complete, so an absent picture is a warning and never a failed store.
        written.Should().BeNull();
        state.LastError.Should().BeEmpty();
        state.LogMessages.Should().ContainSingle(entry => entry.Level == ELogLevel.Warning)
            .Which.Message.Should().StartWith("Relationship view could not be written:");
        TempFiles().Should().BeEmpty();
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

    private RelationshipViewHandler CreateHandler(RecordingPresenter? presenter = null) =>
        new(
            _graph,
            new RelationshipDocumentBuilder(
                new CatalogGraphAnalyzer(),
                new GraphLayoutEngine(),
                new CatalogGraphHtmlRenderer()),
            presenter ?? _presenter,
            new RelationshipPictureWriter(),
            _cursor,
            NullLogger<RelationshipViewHandler>.Instance);

    private void Reading(RelationshipGraphView view) =>
        _graph.ReadCatalogAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(view));

    private static ConnectionInfo Connection() =>
        new(
            Host: "test.example.com",
            Port: 5432,
            DatabaseName: "testdb",
            Username: "test_user",
            Password: "fake-password",
            SslMode: "Prefer");

    private static RelationshipGraphView View() =>
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

    /// <summary>
    /// Two schemas, so the bar offers a real choice: its chips are written in schema order, which makes
    /// the staging chip the second one.
    /// </summary>
    private static RelationshipGraphView TwoSchemas() =>
        CatalogGraphFixture.Create()
            .Table("public", "customers", rowCount: 3, Column("id", "uuid", primaryKey: true))
            .Table("public", "orders", rowCount: 5, Column("id", "uuid", primaryKey: true),
                Column("customer_id", "uuid", referencing: true))
            .Table("staging", "imports", rowCount: 1, Column("id", "uuid", primaryKey: true))
            .Edge(
                CatalogGraphFixture.TableId("public", "orders"),
                CatalogGraphFixture.TableId("public", "customers"),
                "orders_customer_fkey",
                ["customer_id"],
                ["id"],
                mandatory: true,
                onDelete: "cascade")
            .Build();

    private static RelationshipGraphView EmptyDatabase() =>
        CatalogGraphFixture.Create()
            .Live("testdb", "test.example.com:5432/testdb")
            .Build();

    private static RelationshipColumnView Column(
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

    /// <summary>Every file under the root with its size and the moment it was last written.</summary>
    private static Dictionary<string, (long Length, DateTime Written)> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(folder, path),
                path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));

    private static string[] FolderEntries(string folder) =>
        [.. Directory.GetFileSystemEntries(folder)
            .Select(entry => Path.GetFileName(entry)!)
            .Order(StringComparer.Ordinal)];

    private IEnumerable<string> TempFiles() =>
        Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories);

    /// <summary>Records the order the pointer was asked in, so a run's promise can be asserted without a window.</summary>
    private sealed class RecordingCursor : IWaitCursor
    {
        internal List<string> Calls { get; } = [];

        public void Show() => Calls.Add(nameof(Show));

        public void Hide() => Calls.Add(nameof(Hide));
    }

    /// <summary>Records what it was asked to show, so the test can assert on the document and the delegates.</summary>
    private sealed class RecordingPresenter : IRelationshipViewPresenter
    {
        internal List<RelationshipDocument> Presented { get; } = [];

        internal List<RelationshipNotice> Notices { get; } = [];

        internal Func<CancellationToken, Task<RelationshipDocument>>? Refresh { get; private set; }

        internal Func<IReadOnlySet<int>, RelationshipDocument>? Redraw { get; private set; }

        internal Exception? Failure { get; init; }

        /// <summary>Whether the notice reached a window; false is the browser presenter's answer.</summary>
        internal bool AcknowledgesNotice { get; init; } = true;

        public Task<bool> ShowNoticeAsync(RelationshipNotice notice, CancellationToken ct)
        {
            if (Failure is not null) throw Failure;

            Notices.Add(notice);

            return Task.FromResult(AcknowledgesNotice);
        }

        public Task PresentAsync(
            RelationshipDocument document,
            Func<CancellationToken, Task<RelationshipDocument>>? refresh,
            Func<IReadOnlySet<int>, RelationshipDocument>? redraw,
            CancellationToken ct)
        {
            if (Failure is not null) throw Failure;

            Presented.Add(document);
            Refresh = refresh;
            Redraw = redraw;

            return Task.CompletedTask;
        }
    }
}
