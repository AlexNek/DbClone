namespace DbClone.UI.Services.GraphLayout;

/// <summary>A point in the emitted SVG's own coordinates.</summary>
/// <param name="X">The horizontal position.</param>
/// <param name="Y">The vertical position.</param>
internal readonly record struct GraphPoint(double X, double Y);
