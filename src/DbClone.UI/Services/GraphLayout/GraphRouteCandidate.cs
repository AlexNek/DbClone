namespace DbClone.UI.Services.GraphLayout;

/// <summary>One candidate path for a relation, with the ordinal that is part of its route signature.</summary>
/// <param name="Ordinal">The position in the deterministic enumeration; the final tie-breaker.</param>
/// <param name="Geometry">Straight or cubic.</param>
/// <param name="Points">Two points for a straight path, <c>1 + 3n</c> for a cubic.</param>
internal sealed record GraphRouteCandidate(int Ordinal, EEdgeGeometry Geometry, IReadOnlyList<GraphPoint> Points);
