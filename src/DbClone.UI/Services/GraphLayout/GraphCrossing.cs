namespace DbClone.UI.Services.GraphLayout;

/// <summary>One point where two relation centerlines cross.</summary>
/// <param name="FirstEdge">The lower edge index of the pair.</param>
/// <param name="SecondEdge">The higher edge index of the pair.</param>
/// <param name="Point">Where the centerlines meet.</param>
/// <param name="Angle">The acute crossing angle in radians.</param>
/// <param name="IsAdmissible">
/// Whether the crossing is an isolated exceptional crossing under the hard geometry rules: the angle is
/// at least the minimum and the pair shares no run outside the crossing's neighborhood.
/// </param>
internal sealed record GraphCrossing(int FirstEdge, int SecondEdge, GraphPoint Point, double Angle, bool IsAdmissible);
