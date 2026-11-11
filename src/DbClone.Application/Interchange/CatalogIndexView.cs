namespace DbClone.Application.Interchange;

/// <summary>
/// One captured index: its name, its uniqueness, the key columns the producer stated and — when it
/// captured one — the definition text the engine itself reported.
/// </summary>
/// <param name="Name">The index's captured name.</param>
/// <param name="IsUnique">Whether the index is unique.</param>
/// <param name="Definition">
/// The definition text the producer captured verbatim, or <see langword="null"/> when it captured
/// none. Never built here and never parsed: it is the source engine's own words. It is what a restore
/// replays, and it stays useful for what the column list cannot state — an expression index, a
/// partial index's predicate, the access method.
/// </param>
/// <param name="Columns">
/// The key columns in the order the index stores them, or <see langword="null"/> when the folder does
/// not state them — which is what a catalog captured before the field existed reads as. Read straight
/// from the interchange record: never derived from <paramref name="Definition"/> here.
/// </param>
public sealed record CatalogIndexView(
    string Name,
    bool IsUnique,
    string? Definition,
    IReadOnlyList<string>? Columns = null);
