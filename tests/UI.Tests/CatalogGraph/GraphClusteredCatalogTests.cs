using DbClone.UI.Services.GraphAnalysis;
using DbClone.UI.Services.GraphLayout;
using DbClone.UI.Services.Relationship;
using FluentAssertions;
using Xunit.Abstractions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Refactor 18 §6 acceptance, read from the document the application writes for the catalog-shaped fixture and
/// from the engine's own measurement. It asserts §6 items 1, 2, 3, 5 and 6: the large component is clustered into
/// seven groups that win, the picture is neither degraded nor rough, every card and relation is painted with none
/// sent <c>away</c>, the crossings stay under the agreed ceiling, and repair leaves the cards inside their clusters.
/// §6 item 4 (no hidden relations, no merged ports) is deliberately not asserted here — the clustered catalog
/// currently routes relations behind cards they do not join, which is reported rather than tuned around (see the
/// implementation checklist); that regression is tracked by <see cref="GraphRenderedCatalogTests"/>.
/// </summary>
[Collection("Graph quality characterization")]
[Trait("Speed", "Slow")]
public sealed class GraphClusteredCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void The_catalog_picture_is_clustered_whole_and_keeps_its_cards_grouped()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog);
        var builder = new RelationshipDocumentBuilder(new CatalogGraphAnalyzer(), new GraphLayoutEngine(), new CatalogGraphHtmlRenderer());
        var built = builder.Build(GraphQualityFixtures.View(GraphQualityFixtures.Catalog));
        built.IsSuccess.Should().BeTrue("the catalog opens");
        var document = GraphRenderedDocument.Parse(built.Document!.Html);
        output.WriteLine(document.Line());

        // Item 1: the large component was won by the clustered seed, split into the Phase-0 cluster count, and the
        // catalog as a whole stays "assembled" because it has more than one component.
        built.Facts!.ClusteredComponents.Should().Be(1, "one component was drawn as clusters");
        built.Facts.Clusters.Should().Be(7, "the catalog's large component splits into seven clusters");
        built.Facts.Arrangement.Should().Be("assembled", "a catalog of several components is assembled from per-component winners");

        // Item 2: the routed picture the reader asked for, not the bad fallback and not a cut-short search.
        built.IsRough.Should().BeFalse("a catalog of this size must get its routed picture");
        built.IsDegraded.Should().BeFalse("the search finishes the picture inside the application's own watchdog");

        // Item 3: the whole catalog, nothing hidden off the canvas.
        document.PaintedCards.Should().Be(79);
        document.PaintedRelations.Should().Be(96);
        document.HasAway.Should().BeFalse();

        // Item 5: crossings below the agreed ceiling (§6 sets 60; the clustered picture measured well under it).
        document.Crossings.Should().BeLessThanOrEqualTo(60, "clusters keep the relations short and local");

        // Item 6: measured inside the engine — no cluster box lost its cards to repair (rendered page holds no cluster data).
        var moved = RepairMovedOutOfCluster(profile);
        output.WriteLine($"item6: large-component cards moved out of their cluster box = {moved}");
        moved.Should().BeLessThanOrEqualTo(10, "the winning clustered layout keeps its cards inside their clusters");
    }

    private static int RepairMovedOutOfCluster(GraphLayoutProfile profile)
    {
        var component = LargestComponent(profile);
        var nodes = profile.ComponentNodes[component];
        var half = GraphPlacementConstants.ClusterGap / 2;

        var draft = GraphClusteredSeed.Place(profile, default, new GraphEvaluationBudget(GraphSearchBudget.MaximumGeometryEvaluations), null);
        draft.Should().NotBeNull("the catalog's large component is clustered");

        var sub = profile.Subgraph(component);
        var localNodes = Enumerable.Range(0, sub.Nodes.Count).ToArray();
        var localEdges = Enumerable.Range(0, sub.Edges.Count).ToArray();
        var clustered = GraphClusterDetector.Detect(sub.Profile, localNodes, localEdges)!;
        var subNodeArray = sub.Nodes.ToArray();

        var boxes = new (double Left, double Top, double Right, double Bottom)[clustered.Members.Count];
        for (var cluster = 0; cluster < clustered.Members.Count; cluster++)
        {
            var global = clustered.Members[cluster].Select(local => sub.Nodes[local]).ToArray();
            boxes[cluster] = (
                global.Min(node => draft!.Nodes[node].TopLeft.X) - half,
                global.Min(node => draft!.Nodes[node].TopLeft.Y) - half,
                global.Max(node => draft!.Nodes[node].Right) + half,
                global.Max(node => draft!.Nodes[node].Bottom) + half);
        }

        var final = new GraphLayoutSelector().Select(profile, timeLimit: TimeSpan.FromMinutes(2)).Best!;

        // Align both frames on the component's bounding-box origin: this removes only the packing offset the §6
        // item-6 method allows; a refinement that rescaled the component would show up here as moved cards.
        double OriginX(IReadOnlyList<NodePlacement> cards) => nodes.Min(node => cards[node].TopLeft.X);
        double OriginY(IReadOnlyList<NodePlacement> cards) => nodes.Min(node => cards[node].TopLeft.Y);
        var draftX = OriginX(draft.Nodes);
        var draftY = OriginY(draft.Nodes);
        var finalX = OriginX(final.Layout.Nodes);
        var finalY = OriginY(final.Layout.Nodes);

        var moved = 0;
        for (var cluster = 0; cluster < clustered.Members.Count; cluster++)
        {
            var box = (Left: boxes[cluster].Left - draftX, Top: boxes[cluster].Top - draftY, Right: boxes[cluster].Right - draftX, Bottom: boxes[cluster].Bottom - draftY);
            foreach (var local in clustered.Members[cluster])
            {
                var node = subNodeArray[local];
                var centreX = final.Layout.Nodes[node].Centre.X - finalX;
                var centreY = final.Layout.Nodes[node].Centre.Y - finalY;
                if (centreX < box.Left || centreX > box.Right || centreY < box.Top || centreY > box.Bottom)
                {
                    moved++;
                }
            }
        }

        return moved;
    }

    private static int LargestComponent(GraphLayoutProfile profile)
    {
        var largest = 0;
        for (var component = 1; component < profile.ComponentNodes.Count; component++)
        {
            if (profile.ComponentNodes[component].Count > profile.ComponentNodes[largest].Count)
            {
                largest = component;
            }
        }

        return largest;
    }
}
