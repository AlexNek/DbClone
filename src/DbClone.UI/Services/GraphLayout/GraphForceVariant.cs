namespace DbClone.UI.Services.GraphLayout;

/// <summary>One parameter set of the force-directed seed.</summary>
/// <param name="Name">The seed's name.</param>
/// <param name="Spread">How much longer than the card gap the preferred edge length is.</param>
/// <param name="Transposed">Whether the weak rank force acts along x instead of y.</param>
internal sealed record GraphForceVariant(string Name, double Spread, bool Transposed)
{
    /// <summary>Gets the standard variant.</summary>
    public static GraphForceVariant Standard { get; } = new("force", 1.0, false);

    /// <summary>Gets the wider variant.</summary>
    public static GraphForceVariant Wide { get; } = new("force-wide", 1.6, false);

    /// <summary>Gets the variant whose rank direction runs across the page.</summary>
    public static GraphForceVariant Transposed90 { get; } = new("force-transposed", 1.0, true);
}
