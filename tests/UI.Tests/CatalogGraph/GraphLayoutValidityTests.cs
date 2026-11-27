using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>Locks the hard-validity classification on fixed placements (refactor 14, milestone C).</summary>
public sealed class GraphLayoutValidityTests
{
    private static readonly GraphLayoutValidator Validator = new();

    // Card 0 stands left, card 1 right, card 2 between them, cards 3 and 4 above and below the middle.
    private static readonly NodePlacement[] Cards =
    [
        new(0, new GraphPoint(0, 0), 100, 400),
        new(1, new GraphPoint(400, 0), 100, 400),
        new(2, new GraphPoint(200, 80), 30, 40),
        new(3, new GraphPoint(300, -300), 100, 40),
        new(4, new GraphPoint(300, 500), 100, 40),
    ];

    [Fact]
    public void A_chord_passes_behind_a_card_it_does_not_join_and_a_curve_may_not()
    {
        // Card 2 stands between the two cards the relation joins. The cards are painted over the ink, so a
        // straight relation running behind a table is the picture a catalog draws; a curve, which nothing
        // hides, still may not enter a card it does not join.
        Validate([Straight(0, (100, 100), (400, 100))], [new(0, 1)]).IsValid.Should().BeTrue();

        var curve = Validate([Curve(0, (100, 100), (400, 100))], [new(0, 1)]);

        var failure = curve.Failures.Should().ContainSingle().Subject;
        failure.Kind.Should().Be(ERouteFailureKind.CardCollision);
        failure.EdgeIndex.Should().Be(0);
        failure.BlockingCards.Should().Equal(2);
    }

    [Fact]
    public void A_curve_that_clears_the_card_is_valid()
    {
        var curve = Cubic(0, (100, 100), (150, -80), (350, -80), (400, 100));

        var validity = Validate([curve], [new(0, 1)]);

        validity.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_route_may_start_and_end_on_the_border_of_the_cards_it_joins()
    {
        Validate([Straight(0, (100, 390), (400, 390))], [new(0, 1)]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_route_that_passes_through_a_card_it_joins_is_a_collision_naming_that_card()
    {
        // Leaves the right port and sweeps back across the card to a port on its top edge, so it runs over the
        // table it joins. The approach check complains too — the route arrives from inside the card, which is the
        // same defect seen from its other end — so what this pins is that the card rule names the card at all.
        var loop = Cubic(0, (100, 10), (160, 10), (20, 10), (20, 0));

        var validity = Validate([loop], [new(0, 0)]);

        validity.Failures.Should().ContainSingle(failure => failure.Kind == ERouteFailureKind.CardCollision)
            .Which.BlockingCards.Should().Equal(0);
    }

    [Fact]
    public void A_route_that_stays_outside_the_card_clearance_is_valid_and_one_inside_it_is_not()
    {
        var edge = GraphGeometryContract.CardCenterlineClearance;

        Validate([Curve(0, (100, 80 - edge - 0.5), (400, 80 - edge - 0.5))], [new(0, 1)]).IsValid.Should().BeTrue();
        Validate([Curve(0, (100, 80 - edge + 0.5), (400, 80 - edge + 0.5))], [new(0, 1)])
            .Failures.Single().Kind.Should().Be(ERouteFailureKind.CardCollision);
    }

    [Fact]
    public void Two_coincident_curves_are_an_overlap_on_the_later_edge()
    {
        var validity = Validate(
            [Curve(0, (100, 150), (400, 150)), Curve(1, (100, 150), (400, 150))],
            [new(0, 1), new(0, 1)]);

        var failure = validity.Failures.Should().ContainSingle().Subject;
        failure.Kind.Should().Be(ERouteFailureKind.StrokeOverlap);
        failure.EdgeIndex.Should().Be(1);
        failure.BlockingRoutes.Should().Equal(0);
    }

    [Fact]
    public void A_near_parallel_run_closer_than_the_centerline_distance_is_an_overlap()
    {
        var close = GraphGeometryContract.MinimumCenterlineDistance - 1;
        var validity = Validate(
            [Curve(0, (100, 150), (400, 150)), Curve(1, (100, 150 + close), (400, 150 + close))],
            [new(0, 1), new(0, 1)]);

        validity.Failures.Single().Kind.Should().Be(ERouteFailureKind.StrokeOverlap);
    }

    [Fact]
    public void Parallel_routes_a_port_step_apart_are_valid()
    {
        var validity = Validate(
            [Straight(0, (100, 150), (400, 150)), Straight(1, (100, 150 + GraphGeometryContract.MinimumPortStep), (400, 150 + GraphGeometryContract.MinimumPortStep))],
            [new(0, 1), new(0, 1)]);

        validity.IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_isolated_right_angle_crossing_is_counted_and_is_not_an_overlap()
    {
        var validity = Validate(
            [Straight(0, (100, 150), (400, 150)), Straight(1, (330, -260), (330, 500))],
            [new(0, 1), new(3, 4)]);

        validity.IsValid.Should().BeTrue();
        var crossing = validity.Crossings.Should().ContainSingle().Subject;
        crossing.IsAdmissible.Should().BeTrue();
        crossing.Angle.Should().BeApproximately(Math.PI / 2, 1e-9);
        crossing.Point.Should().Be(new GraphPoint(330, 150));
        validity.AdmissibleCrossingCount.Should().Be(1);
    }

    [Fact]
    public void A_shallow_crossing_whose_close_run_outlasts_the_neighborhood_is_a_shared_route()
    {
        var validity = Validate(
            [Curve(0, (100, 290), (400, 310)), Curve(1, (100, 310), (400, 290))],
            [new(0, 1), new(0, 1)]);

        validity.Failures.Single().Kind.Should().Be(ERouteFailureKind.StrokeOverlap);
        validity.Crossings.Should().ContainSingle().Which.IsAdmissible.Should().BeFalse();
    }

    [Fact]
    public void A_crossing_below_the_minimum_angle_is_a_failure_even_with_no_long_run()
    {
        var rise = 300 * Math.Tan(Math.PI / 20);
        var validity = Validate(
            [Curve(0, (100, 200), (400, 200 + rise)), Curve(1, (100, 200 + rise), (400, 200))],
            [new(0, 1), new(0, 1)]);

        validity.Failures.Single().Kind.Should().Be(ERouteFailureKind.ShallowCrossing);
        validity.Crossings.Single().Angle.Should().BeLessThan(GraphGeometryContract.MinimumCrossingAngle);
    }

    [Fact]
    public void A_non_finite_coordinate_is_reported_and_the_route_is_not_checked_further()
    {
        var validity = Validate([Straight(0, (100, 100), (double.NaN, 100))], [new(0, 1)]);

        validity.Failures.Single().Kind.Should().Be(ERouteFailureKind.NonFiniteCoordinate);
    }

    [Fact]
    public void A_curve_that_reaches_the_depth_bound_unflattened_is_a_precision_failure()
    {
        var curve = Cubic(0, (100, 100), (1e12, 1e12), (-1e12, 1e12), (400, 100));

        Validate([curve], [new(0, 1)]).Failures.Single().Kind.Should().Be(ERouteFailureKind.GeometryPrecisionLimit);
    }

    [Fact]
    public void A_route_that_runs_along_its_card_border_has_an_invalid_approach()
    {
        var validity = Validate([Straight(0, (100, 300), (400, 350))], [new(0, 1)]);
        validity.IsValid.Should().BeTrue("a slanted but outward route is a valid approach");

        var along = Validate([Cubic(0, (100, 250), (100, 350), (350, 350), (400, 250))], [new(0, 1)]);
        along.Failures.Single().Kind.Should().Be(ERouteFailureKind.InvalidPortApproach);
    }

    [Fact]
    public void A_route_that_does_not_touch_its_card_has_an_invalid_approach()
    {
        Validate([Straight(0, (150, 100), (400, 100))], [new(0, 1)])
            .Failures.Should().Contain(failure => failure.Kind == ERouteFailureKind.InvalidPortApproach);
    }

    [Fact]
    public void The_same_layout_reports_the_same_failures_twice()
    {
        var routes = new[] { Straight(0, (100, 100), (400, 100)), Straight(1, (100, 100), (400, 100)) };
        var ends = new GraphEdgeEndpoints[] { new(0, 1), new(0, 1) };

        var first = Validate(routes, ends);
        var second = Validate(routes, ends);

        second.Failures.Select(f => (f.EdgeIndex, f.Kind)).Should().Equal(first.Failures.Select(f => (f.EdgeIndex, f.Kind)));
    }

    [Fact]
    public void A_route_that_crosses_itself_is_rejected()
    {
        // Two stretches of the same relation meet in the open, so the relation would be drawn as a loop
        // that belongs to nobody: only a self-reference's closed shape may meet its own ends.
        var crossing = new EdgeRoute(
            0,
            EEdgeShape.Line,
            EEdgeGeometry.Straight,
            [new(100, 100), new(400, 300), new(400, 100), new(100, 300), new(400, 300)],
            new GraphPoint(0, 0));

        var validity = Validate([crossing], [new(0, 1)]);

        validity.Failures.Should().ContainSingle().Which.Kind.Should().Be(ERouteFailureKind.SelfIntersection);
    }

    [Fact]
    public void A_smooth_route_whose_segments_meet_only_at_their_join_is_valid()
    {
        var curve = Cubic(0, (100, 300), (150, 250), (250, 250), (250, 300), (250, 350), (350, 350), (400, 300));

        Validate([curve], [new(0, 1)]).IsValid.Should().BeTrue();
    }

    private static GraphLayoutValidity Validate(EdgeRoute[] routes, GraphEdgeEndpoints[] ends) =>
        Validator.Validate(Cards, routes, ends);

    private static EdgeRoute Straight(int index, (double X, double Y) start, (double X, double Y) end) =>
        new(index, EEdgeShape.Line, EEdgeGeometry.Straight, [new(start.X, start.Y), new(end.X, end.Y)], new GraphPoint(0, 0));

    /// <summary>
    /// A smooth route along the same line as a straight one. Curves keep the strict rules — nothing is
    /// painted over them — so the tests that pin those rules draw their routes this way.
    /// </summary>
    private static EdgeRoute Curve(int index, (double X, double Y) start, (double X, double Y) end) =>
        Cubic(
            index,
            start,
            (start.X + ((end.X - start.X) / 3), start.Y + ((end.Y - start.Y) / 3)),
            (start.X + (2 * (end.X - start.X) / 3), start.Y + (2 * (end.Y - start.Y) / 3)),
            end);

    private static EdgeRoute Cubic(int index, params (double X, double Y)[] points) =>
        new(index, EEdgeShape.Line, EEdgeGeometry.Cubic, points.Select(point => new GraphPoint(point.X, point.Y)).ToArray(), new GraphPoint(0, 0));
}
