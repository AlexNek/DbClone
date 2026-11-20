using System.Text.RegularExpressions;
using DbClone.Application.Relationship;
using DbClone.UI.Services;
using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks how the writer serialises each route geometry (refactor 14, milestone B).</summary>
public sealed class GraphPictureWriterRouteTests
{
    private static readonly DateTimeOffset Moment = new(2026, 11, 10, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_straight_route_is_one_line_segment()
    {
        var (visible, pick) = PathsOf(EEdgeGeometry.Straight, [new(10, 20), new(110.5, 20)]);

        visible.Should().Be("M 10 20 L 110.5 20");
        pick.Should().Be(visible, "the pick path uses exactly the visible geometry");
    }

    [Fact]
    public void A_cubic_route_is_one_curve_command()
    {
        var (visible, _) = PathsOf(EEdgeGeometry.Cubic, [new(0, 0), new(0, 50), new(100, 50), new(100, 0)]);

        visible.Should().Be("M 0 0 C 0 50 100 50 100 0");
    }

    [Fact]
    public void A_multi_segment_cubic_route_is_connected_curve_commands_without_lines()
    {
        var (visible, pick) = PathsOf(
            EEdgeGeometry.Cubic,
            [new(0, 0), new(0, 50), new(50, 50), new(50, 0), new(50, -50), new(100, -50), new(100, 0)]);

        visible.Should().Be("M 0 0 C 0 50 50 50 50 0 C 50 -50 100 -50 100 0");
        visible.Should().NotContain(" L ");
        pick.Should().Be(visible);
    }

    [Fact]
    public void A_loop_is_a_closed_cubic_path()
    {
        var (visible, _) = PathsOf(
            EEdgeGeometry.Loop,
            [new(0, 0), new(30, -40), new(60, -40), new(60, 0), new(60, 40), new(30, 40), new(0, 0)]);

        visible.Should().StartWith("M 0 0 C ").And.EndWith("0 0");
        Regex.Matches(visible, " C ").Count.Should().Be(1 + 1, "the first C follows M and a second one closes the loop");
    }

    private static (string Visible, string Pick) PathsOf(EEdgeGeometry geometry, GraphPoint[] points)
    {
        var view = CatalogGraphFixture.Create()
            .Table("public", "orders", 10, Column("id"))
            .Table("public", "customers", 10, Column("id"))
            .Edge(
                CatalogGraphFixture.TableId("public", "orders"),
                CatalogGraphFixture.TableId("public", "customers"),
                "fk_orders_customers",
                ["customer_id"],
                ["id"])
            .Build();
        var model = new CatalogGraphAnalyzer().Analyze(view);
        var layout = new GraphLayoutEngine().Compute(model).Layout!;
        var route = layout.Edges[0] with { Geometry = geometry, Points = points, LabelPoint = points[0] };
        var edges = layout.Edges.ToArray();
        edges[0] = route;

        var document = new CatalogGraphHtmlRenderer().Render(view, model, layout with { Edges = edges }, Moment);
        var paths = Regex.Matches(document, "<path class=\"edge[^\"]*\" data-e=\"0\" d=\"([^\"]*)\"");

        return (paths[0].Groups[1].Value, paths[1].Groups[1].Value);
    }

    private static RelationshipColumnView Column(string name) =>
        new(name, "integer", false, true, false, false, null);
}
