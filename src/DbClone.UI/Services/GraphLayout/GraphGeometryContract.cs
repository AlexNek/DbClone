namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The one set of numbers routing, validation, scoring, label placement and tests agree on. Nothing here
/// is a user setting. A route is visually separate from another only when its centerlines meet
/// <see cref="MinimumCenterlineDistance"/>; the visible gap between two strokes is never confused with
/// the distance between their centerlines.
/// </summary>
internal static class GraphGeometryContract
{
    /// <summary>The visible width of a relation stroke. The stylesheet's <c>.edge</c> rule takes this value.</summary>
    public const double StrokeWidth = 1.3;

    /// <summary>The space that must stay visibly empty between two rendered strokes.</summary>
    public const double MinimumVisibleStrokeGap = 4;

    /// <summary>The least distance between two relation centerlines.</summary>
    public const double MinimumCenterlineDistance = StrokeWidth + MinimumVisibleStrokeGap;

    /// <summary>The width of the ink an arrow head prints, which is much wider than the stroke that carries it.</summary>
    public const double ArrowHeadInkWidth = 14;

    /// <summary>
    /// The along-border pitch of two ports. It is a head-to-head measure, so it is larger than
    /// <see cref="MinimumCenterlineDistance"/>: two ends nearer than this print as one thick mark.
    /// </summary>
    public const double MinimumPortStep = ArrowHeadInkWidth + MinimumVisibleStrokeGap;

    /// <summary>The run length over which a close pair of routes counts as an overlap.</summary>
    public const double MinimumSharedRun = 12;

    /// <summary>The smallest angle, in radians, at which two routes may cross as an isolated crossing (20 degrees).</summary>
    public const double MinimumCrossingAngle = Math.PI / 9;

    /// <summary>The length around a classified crossing that shared-run detection leaves out.</summary>
    public const double CrossingNeighborhoodLength = 12;

    /// <summary>The empty space intended between a card edge and the visible edge of a stroke.</summary>
    public const double CardClearance = 5;

    /// <summary>The distance withheld from each card corner when ports are placed.</summary>
    public const double CardCornerClearance = 8;

    /// <summary>The largest deviation adaptive flattening may leave between a curve and its chord.</summary>
    public const double CurveFlatnessTolerance = 0.25;

    /// <summary>The deterministic safety bound on adaptive subdivision.</summary>
    public const int MaximumCurveSubdivisionDepth = 12;

    /// <summary>
    /// The distance from a card edge to a centerline that the validator enforces:
    /// <see cref="CardClearance"/> plus half the stroke.
    /// </summary>
    public const double CardCenterlineClearance = CardClearance + (StrokeWidth / 2);
}
