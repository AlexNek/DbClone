namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// A cubic route flattened into short straight segments for collision tests. An incomplete result means
/// subdivision reached its depth bound while the flatness tolerance was still exceeded
/// (<c>GeometryPrecisionLimit</c>); it must never be used as valid collision geometry.
/// </summary>
/// <param name="Points">The flattened points, first to last.</param>
/// <param name="IsComplete">Whether every segment met the flatness tolerance.</param>
internal sealed record GraphFlatCurve(IReadOnlyList<GraphPoint> Points, bool IsComplete);
