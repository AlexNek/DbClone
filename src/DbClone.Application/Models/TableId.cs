namespace DbClone.Application.Models;

/// <summary>
/// Structured identity of a table (schema + name).
/// Used instead of dot-joined strings so table selection never parses identifiers;
/// dot-joined forms in metadata (view dependencies, sequence owners, partition parents)
/// are converted to <see cref="TableId"/> at a single, documented boundary.
/// </summary>
public sealed record TableId(string Schema, string Name)
{
    /// <summary>
    /// Dot-joined form for display and logging only. Never parsed back into a <see cref="TableId"/>.
    /// </summary>
    public string FullName => $"{Schema}.{Name}";

    /// <summary>Case-insensitive equality — PostgreSQL unquoted identifiers fold to lowercase.</summary>
    public bool Equals(TableId? other) =>
        other is not null
        && string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Schema),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    /// <inheritdoc />
    public override string ToString() => FullName;
}
