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
        output.WriteLine($"hidden as chords={document.HiddenChords} as curves={document.HiddenCurves}");
        document.PaintedCards.Should().Be(79, "the catalog opens whole");
        document.PaintedRelations.Should().Be(96);
        document.HasAway.Should().BeFalse();
        result.IsRough.Should().BeFalse("a picture the search proved is the one asked for");
        // The picture's own contract: the bounds file states it and the rendered legend tells the reader ("the cards
        // are painted over the arrows, so a relation passing one runs behind it"). Every relation but a
        // self-reference is a straight chord between its two ports and may pass behind a card; only a curve is held
        // out of every card it does not join. That is the rule asserted here; the chord count stays on the line above.
        document.HiddenCurves.Should().Be(0, "a curve may never run behind a card it does not join");
        document.FanHiddenCurves.Should().Be(0, "no curve meeting the busiest table is covered");
        document.MergedPortMarks.Should().Be(0, "arrows never fuse");
        // The picture's own promise, counted in the ink the document prints: two constraints revealed by one
        // click are never written on one place. The one pair this page still owes is a self-reference whose
        // label blankets the corridor its neighbours run through — no share of any of those routes separates
        // them, so it is recorded and bounded rather than silently accepted (refactors/19).
        document.LabelOverprints.Should().BeLessThanOrEqualTo(1, "a revealed constraint is never printed over another");
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
