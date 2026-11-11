namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// One cluster chip. A chip reveals and hides; it never removes anything from the file, so its
/// member list is what the picture shows and hides, not what the document holds.
/// </summary>
/// <param name="Index">The chip's position in the emitted chip bar; the markup token.</param>
/// <param name="Kind">Which of the three groups the chip belongs to.</param>
/// <param name="Label">The words on the chip.</param>
/// <param name="MemberNodeIndexes">The nodes the chip claims, in canonical order.</param>
internal sealed record CatalogClusterChip(
    int Index,
    EClusterChipKind Kind,
    string Label,
    IReadOnlyList<int> MemberNodeIndexes);
