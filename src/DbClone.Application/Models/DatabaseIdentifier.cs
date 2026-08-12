namespace DbClone.Application.Models;

/// <summary>
/// Identifies the database a table selection preset belongs to:
/// a saved connection profile plus the database name.
/// Two profiles pointing at the same physical database keep separate presets.
/// </summary>
public sealed record DatabaseIdentifier(string ConnectionProfileId, string DatabaseName)
{
    /// <summary>Case-insensitive match — PostgreSQL database names fold like identifiers.</summary>
    public bool Matches(DatabaseIdentifier? other) =>
        other is not null
        && string.Equals(ConnectionProfileId, other.ConnectionProfileId, StringComparison.Ordinal)
        && string.Equals(DatabaseName, other.DatabaseName, StringComparison.OrdinalIgnoreCase);
}
