namespace CanonicalModel.Models;

/// <summary>
/// Represents a publication.
/// </summary>
public sealed record PublicationDefinition(
    string Name,
    bool IsForAllTables,
    IReadOnlyList<string> Tables,
    string? Comment);
