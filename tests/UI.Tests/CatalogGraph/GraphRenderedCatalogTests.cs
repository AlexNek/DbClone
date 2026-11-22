using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;
using DbClone.UI.Services.Relationship;
using FluentAssertions;
using Xunit.Abstractions;
namespace UI.Tests.CatalogGraph;
/// <summary>
/// The acceptance of refactor 17, read from the document the application writes for the catalog-shaped
/// fixture: every table and relation painted, nothing hidden by <c>away</c>, the terms taken from the markup
/// and not from an engine counter.
/// </summary>
[Collection("Graph quality characterization")]
[Trait("Speed", "Slow")]
public sealed class GraphRenderedCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void The_catalog_document_is_read_from_its_own_markup()
    {
        var builder = new RelationshipDocumentBuilder(new CatalogGraphAnalyzer(), new GraphLayoutEngine(), new CatalogGraphHtmlRenderer());
        var result = builder.Build(GraphQualityFixtures.View(GraphQualityFixtures.Catalog));
        result.IsSuccess.Should().BeTrue();
        var document = GraphRenderedDocument.Parse(result.Document!.Html);
        output.WriteLine($"degraded={result.IsDegraded} rough={result.IsRough} arrangement={result.Facts?.Arrangement} seeds={result.Facts?.Seeds}");
        output.WriteLine(document.Line());
        output.WriteLine("hidden relations: " + string.Join(", ", document.HiddenRelationNames));
        document.PaintedCards.Should().Be(79, "the catalog opens whole");
        document.PaintedRelations.Should().Be(96);
        document.HasAway.Should().BeFalse();
        document.HiddenMeetingBusiestTable.Should().Be(0, "the busiest table's fan is whole");
        document.MergedPortMarks.Should().Be(0, "arrows never fuse");
        document.Crossings.Should().BeLessThanOrEqualTo(150);
    }

    [Fact]
    public void The_engine_measure_agrees_with_the_page_it_produced()
    {
        var view = GraphQualityFixtures.View(GraphQualityFixtures.Catalog);
        var model = new CatalogGraphAnalyzer().Analyze(view);
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog);
        var outcome = new GraphLayoutSelector().Select(profile);
        outcome.IsSuccess.Should().BeTrue();
        var best = outcome.Best!;
        var html = new CatalogGraphHtmlRenderer().Render(
            view,
            model,
            new CatalogGraphLayout(best.Layout.Nodes, best.Routes, best.Layout.Width, best.Layout.Height),
            DateTimeOffset.UnixEpoch);
        var page = GraphRenderedDocument.Parse(html);
        var engine = GraphReaderMeasures.Of(best, profile);
        output.WriteLine("engine: " + engine.Line());
        output.WriteLine("page:   " + page.Line());
        engine.HiddenStrokes.Should().Be(page.HiddenRelations);
        engine.OccludingCards.Should().Be(page.DistinctOccludingCards);
        engine.BusiestFanHidden.Should().Be(page.HiddenMeetingBusiestTable);
        engine.LabelsOnCards.Should().Be(page.LabelsOnCards);
    }
}
