using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// Pins the two catalog-scale shapes the layout is measured on but never judges: what a reader cannot check by
/// eye at seventy-nine tables is the shape itself, so the counts a fixture summary states are asserted here. A
/// fixture whose edges drift from its own description fails here before it can be read as evidence.
/// </summary>
[Trait("Speed", "Fast")]
public sealed class GraphQualityFixturesTests
{
    private const int CatalogTables = 79;

    [Fact]
    public void The_clusters_shape_is_two_settlements_of_thirty_nine_tables_joined_by_three_relations()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Clusters);
        var firstSettlement = Enumerable.Range(0, 39).ToHashSet();

        profile.NodeCount.Should().Be(CatalogTables);
        profile.Edges.Should().HaveCount(79);
        profile.ComponentNodes.Select(component => component.Count).Should().BeEquivalentTo(new[] { 78, 1 },
            "the two settlements are joined to one another, so the profile sees one component and the untouched table");

        profile.Edges
            .Count(edge => firstSettlement.Contains(edge.Child) != firstSettlement.Contains(edge.Parent))
            .Should().Be(3, "the settlements are joined by three relations and no more");
        profile.Edges
            .Count(edge => edge.Child == 78 || edge.Parent == 78)
            .Should().Be(0, "the last table is the one no relation reaches");
    }

    [Fact]
    public void The_catalog_shape_is_the_reported_catalogs_graph()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Catalog);
        var ends = new int[profile.NodeCount];
        foreach (var (child, parent) in profile.Edges)
        {
            ends[child]++;
            ends[parent]++;
        }
        profile.NodeCount.Should().Be(CatalogTables);
        profile.Edges.Should().HaveCount(96);
        profile.Edges.Count(edge => edge.Child == edge.Parent).Should().Be(8, "eight tables reference themselves");
        ends.Max().Should().Be(27, "one table is the one the catalog is built around");
        ends.Count(value => value == 0).Should().Be(17, "seventeen tables no relation reaches");
    }

    [Fact]
    public void The_ring_shape_is_a_ring_of_seventy_eight_tables_with_one_chord()
    {
        var profile = GraphQualityFixtures.Profile(GraphQualityFixtures.Ring);
        var degree = new int[profile.NodeCount];
        foreach (var (child, parent) in profile.Edges)
        {
            degree[child]++;
            degree[parent]++;
        }

        profile.NodeCount.Should().Be(CatalogTables);
        profile.Edges.Should().HaveCount(79);
        profile.ComponentNodes.Select(component => component.Count).Should().BeEquivalentTo(new[] { 78, 1 });

        degree.Count(value => value == 3).Should().Be(2, "one chord leaves the ring's own two tables with a third relation");
        degree.Count(value => value == 2).Should().Be(76, "the rest of the ring is joined to its two neighbours and nothing else");
        degree[78].Should().Be(0, "the last table is the one no relation reaches");
    }
}
