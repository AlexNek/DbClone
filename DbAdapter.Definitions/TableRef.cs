namespace DbAdapter.Definitions;

/// <summary>
/// Identifies a table by schema and name.
/// </summary>
/// <param name="Schema">The schema name.</param>
/// <param name="Name">The table name.</param>
public sealed record TableRef(string Schema, string Name)
{
    /// <inheritdoc />
    public override string ToString() => $"{Schema}.{Name}";
}
