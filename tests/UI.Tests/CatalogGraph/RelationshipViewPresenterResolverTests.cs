using DbClone.Application.Relationship;
using DbClone.UI.Services.Relationship;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// D4: how a document is shown is decided per request. A machine without the runtime still gets the
/// view, in its browser; a machine with it gets the app's window, and a window already open keeps
/// taking documents even if the runtime stops answering the probe.
/// </summary>
/// <remarks>
/// The collaborators are internal, so they are recorded by hand rather than substituted: the decision
/// under test is one branch, and a recorder says plainly which side took the document and whether the
/// probe was consulted at all.
/// </remarks>
public sealed class RelationshipViewPresenterResolverTests
{
    [Fact]
    public async Task Without_a_runtime_the_browser_takes_the_document()
    {
        // Arrange
        var probe = new Probe(available: false);
        var window = new Window(hasWindow: false);
        var browser = new Recorder();

        // Act
        await CreateResolver(probe, window, browser).PresentAsync(Document(), refresh: null, redraw: null, CancellationToken.None);

        // Assert
        browser.Presented.Should().Be(1);
        window.Presented.Should().Be(0);
        probe.Asked.Should().Be(1);
    }

    [Fact]
    public async Task With_a_runtime_the_window_takes_the_document()
    {
        // Arrange
        var probe = new Probe(available: true);
        var window = new Window(hasWindow: false);
        var browser = new Recorder();

        // Act
        await CreateResolver(probe, window, browser).PresentAsync(Document(), refresh: null, redraw: null, CancellationToken.None);

        // Assert
        window.Presented.Should().Be(1);
        browser.Presented.Should().Be(0);
    }

    [Fact]
    public async Task A_window_that_is_already_open_keeps_the_document_without_asking_the_probe()
    {
        // Arrange — the runtime disappeared after the window opened; the window is still the reader's.
        var probe = new Probe(available: false);
        var window = new Window(hasWindow: true);
        var browser = new Recorder();

        // Act
        await CreateResolver(probe, window, browser).PresentAsync(Document(), refresh: null, redraw: null, CancellationToken.None);

        // Assert
        window.Presented.Should().Be(1);
        browser.Presented.Should().Be(0);
        probe.Asked.Should().Be(0);
    }

    private static RelationshipViewPresenterResolver CreateResolver(
        IWebViewRuntimeProbe probe,
        IInAppRelationshipPresenter window,
        IRelationshipViewPresenter browser) => new(probe, window, browser);

    private static RelationshipDocument Document() =>
        new(
            new GraphViewOrigin(
                Kind: EGraphOriginKind.Catalog,
                Label: "fixture",
                Location: @"C:\catalogs\fixture",
                Dialect: "postgresql",
                FormatVersion: 3),
            "<!DOCTYPE html>");

    /// <summary>The runtime's answer, and how often it was asked for it.</summary>
    private sealed class Probe(bool available) : IWebViewRuntimeProbe
    {
        public int Asked { get; private set; }

        public bool IsAvailable()
        {
            Asked++;

            return available;
        }
    }

    /// <summary>A window that states whether it is open, and counts the documents it took.</summary>
    private sealed class Window(bool hasWindow) : IInAppRelationshipPresenter
    {
        public int Presented { get; private set; }

        public bool HasWindow => hasWindow;

        public Task PresentAsync(
            RelationshipDocument document,
            Func<CancellationToken, Task<RelationshipDocument>>? refresh,
            Func<IReadOnlySet<int>, RelationshipDocument>? redraw,
            CancellationToken ct)
        {
            Presented++;

            return Task.CompletedTask;
        }
    }

    /// <summary>The fallback, which only has to say whether the document reached it.</summary>
    private sealed class Recorder : IRelationshipViewPresenter
    {
        public int Presented { get; private set; }

        public Task PresentAsync(
            RelationshipDocument document,
            Func<CancellationToken, Task<RelationshipDocument>>? refresh,
            Func<IReadOnlySet<int>, RelationshipDocument>? redraw,
            CancellationToken ct)
        {
            Presented++;

            return Task.CompletedTask;
        }
    }
}
