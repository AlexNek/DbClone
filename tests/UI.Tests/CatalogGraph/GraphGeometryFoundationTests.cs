using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks the shared geometry every router, validator, score and test reads (refactor 14, milestone A).</summary>
public sealed class GraphGeometryFoundationTests
{
    [Fact]
    public void The_constants_state_one_consistent_contract()
    {
        GraphGeometryContract.MinimumCenterlineDistance.Should().Be(
            GraphGeometryContract.StrokeWidth + GraphGeometryContract.MinimumVisibleStrokeGap);
        GraphGeometryContract.MinimumPortStep.Should().BeGreaterThanOrEqualTo(GraphGeometryContract.MinimumCenterlineDistance);
        GraphGeometryContract.MinimumPortStep.Should().Be(
            GraphGeometryContract.ArrowHeadInkWidth + GraphGeometryContract.MinimumVisibleStrokeGap);
        GraphGeometryContract.CardCenterlineClearance.Should().Be(
            GraphGeometryContract.CardClearance + (GraphGeometryContract.StrokeWidth / 2));
    }

    [Fact]
    public void The_stylesheet_edge_width_is_the_geometry_constant()
    {
        var width = GraphGeometryContract.StrokeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);

        DbClone.UI.Services.GraphHtml.CatalogGraphStyles.Css.Should().Contain($".edge{{fill:none;stroke:#8b949e;stroke-width:{width}}}");
    }

    [Fact]
    public void The_placement_constants_keep_their_present_values()
    {
        GraphPlacementConstants.ColumnGap.Should().Be(120);
        GraphPlacementConstants.Margin.Should().Be(120);
        GraphPlacementConstants.ComponentGap.Should().Be(80);
    }

    [Fact]
    public void A_straight_looking_cubic_flattens_to_its_two_ends()
    {
        var flat = GraphGeometry.Flatten([new(0, 0), new(10, 0), new(20, 0), new(30, 0)]);

        flat.IsComplete.Should().BeTrue();
        flat.Points.Should().Equal(new GraphPoint(0, 0), new GraphPoint(30, 0));
    }

    [Fact]
    public void A_bowed_cubic_flattens_within_the_tolerance()
    {
        var controls = new GraphPoint[] { new(0, 0), new(0, 100), new(100, 100), new(100, 0) };

        var flat = GraphGeometry.Flatten(controls);

        flat.IsComplete.Should().BeTrue();
        flat.Points.Count.Should().BeGreaterThan(4);
        for (var step = 0; step <= 20; step++)
        {
            var onCurve = GraphGeometry.CubicAt(controls[0], controls[1], controls[2], controls[3], step / 20.0);
            var nearest = Enumerable.Range(1, flat.Points.Count - 1)
                .Min(index => GraphGeometry.DistanceToSegment(onCurve, flat.Points[index - 1], flat.Points[index]));
            nearest.Should().BeLessThanOrEqualTo(GraphGeometryContract.CurveFlatnessTolerance + 1e-9);
        }
    }

    [Fact]
    public void A_multi_segment_sequence_is_flattened_through_every_segment()
    {
        var flat = GraphGeometry.Flatten(
            [new(0, 0), new(0, 50), new(50, 50), new(50, 0), new(50, -50), new(100, -50), new(100, 0)]);

        flat.IsComplete.Should().BeTrue();
        flat.Points[0].Should().Be(new GraphPoint(0, 0));
        flat.Points[^1].Should().Be(new GraphPoint(100, 0));
        flat.Points.Should().Contain(new GraphPoint(50, 0));
    }

    [Fact]
    public void A_curve_that_cannot_be_flattened_in_the_depth_bound_is_incomplete()
    {
        var flat = GraphGeometry.Flatten([new(0, 0), new(1e12, 1e12), new(-1e12, 1e12), new(1, 0)]);

        flat.IsComplete.Should().BeFalse("GeometryPrecisionLimit must reject the candidate");
    }

    [Fact]
    public void A_sequence_that_is_not_one_plus_three_n_points_is_refused()
    {
        var act = () => GraphGeometry.Flatten([new(0, 0), new(1, 1), new(2, 2)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_point_is_measured_against_the_segment_not_the_line()
    {
        GraphGeometry.DistanceToSegment(new(5, 3), new(0, 0), new(10, 0)).Should().BeApproximately(3, 1e-9);
        GraphGeometry.DistanceToSegment(new(13, 4), new(0, 0), new(10, 0)).Should().BeApproximately(5, 1e-9);
    }

    [Fact]
    public void Two_segments_report_their_meeting_point_and_distance()
    {
        GraphGeometry.TryIntersection(new(0, 0), new(10, 10), new(0, 10), new(10, 0), out var point).Should().BeTrue();
        point.X.Should().BeApproximately(5, 1e-9);
        point.Y.Should().BeApproximately(5, 1e-9);
        GraphGeometry.SegmentDistance(new(0, 0), new(10, 10), new(0, 10), new(10, 0)).Should().Be(0);
        GraphGeometry.SegmentDistance(new(0, 0), new(10, 0), new(0, 4), new(10, 4)).Should().BeApproximately(4, 1e-9);
    }

    [Fact]
    public void Collinear_segments_are_a_shared_run_and_not_an_intersection_point()
    {
        GraphGeometry.TryIntersection(new(0, 0), new(10, 0), new(5, 0), new(15, 0), out _).Should().BeFalse();
        GraphGeometry.SegmentDistance(new(0, 0), new(10, 0), new(5, 0), new(15, 0)).Should().Be(0);
    }

    [Fact]
    public void The_crossing_angle_is_acute_and_symmetric()
    {
        GraphGeometry.CrossingAngle(new(0, 0), new(10, 0), new(5, -5), new(5, 5)).Should().BeApproximately(Math.PI / 2, 1e-9);
        GraphGeometry.CrossingAngle(new(0, 0), new(10, 0), new(0, 0), new(-10, 0)).Should().BeApproximately(0, 1e-9);
        GraphGeometry.CrossingAngle(new(0, 0), new(10, 10), new(10, 0), new(0, 10)).Should().BeApproximately(Math.PI / 2, 1e-9);
        GraphGeometry.CrossingAngle(new(0, 0), new(10, 0), new(0, 0), new(10, 10)).Should().BeApproximately(Math.PI / 4, 1e-9);
    }

    [Fact]
    public void Arclength_and_its_midpoint_follow_the_curve()
    {
        var polyline = new GraphPoint[] { new(0, 0), new(10, 0), new(10, 10) };

        GraphGeometry.ArcLength(polyline).Should().BeApproximately(20, 1e-9);
        GraphGeometry.ArcPoint(polyline, 0.5).Should().Be(new GraphPoint(10, 0));
        GraphGeometry.ArcPoint(polyline, 0.75).Y.Should().BeApproximately(5, 1e-9);
    }

    [Fact]
    public void A_card_envelope_is_grown_by_clearance_plus_half_the_stroke()
    {
        var card = new NodePlacement(0, new GraphPoint(100, 100), 100, 40);
        var inside = GraphGeometryContract.CardCenterlineClearance - 0.1;
        var outside = GraphGeometryContract.CardCenterlineClearance + 0.1;

        GraphGeometry.SegmentHitsCardEnvelope(new(0, 100 - inside), new(400, 100 - inside), card).Should().BeTrue();
        GraphGeometry.SegmentHitsCardEnvelope(new(0, 100 - outside), new(400, 100 - outside), card).Should().BeFalse();
    }
}
