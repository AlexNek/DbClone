namespace CanonicalModel.Interchange;

/// <summary>
/// The native type of a column or value as reported by the source engine.
/// This is the exact text, with no parsing or normalization.
/// </summary>
/// <param name="NativeName">The engine's own type name (e.g., "varchar(100)", "timestamptz").</param>
public sealed record NativeType(string NativeName)
{
    /// <summary>Gets the native type name.</summary>
    public string Name => NativeName;

    /// <inheritdoc />
    public override string ToString() => NativeName;
}
