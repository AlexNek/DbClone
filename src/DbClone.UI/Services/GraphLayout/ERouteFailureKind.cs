namespace DbClone.UI.Services.GraphLayout;

/// <summary>Why a relation or a layout is not valid. A failure is explicit; it is never repaired silently.</summary>
internal enum ERouteFailureKind
{
    /// <summary>The route enters a card. A card the relation does not join is grown by the card clearance; the two cards it joins are held to their border, so it may not run over its own table either.</summary>
    CardCollision,

    /// <summary>The route shares a run, or runs near-parallel closer than the centerline distance, with another route.</summary>
    StrokeOverlap,

    /// <summary>The route crosses another at an angle below the minimum, so it is not an isolated crossing.</summary>
    ShallowCrossing,

    /// <summary>A card border cannot hold the ends that meet it.</summary>
    PortCapacity,

    /// <summary>No admissible candidate route exists for the relation.</summary>
    NoCandidate,

    /// <summary>The route does not leave or enter its card along the border's outward normal, or does not touch its card.</summary>
    InvalidPortApproach,

    /// <summary>The route crosses itself: only the closed loop shape may meet its own ends.</summary>
    SelfIntersection,

    /// <summary>A coordinate is not finite.</summary>
    NonFiniteCoordinate,

    /// <summary>Adaptive flattening reached its depth bound before meeting the flatness tolerance.</summary>
    GeometryPrecisionLimit,
}
