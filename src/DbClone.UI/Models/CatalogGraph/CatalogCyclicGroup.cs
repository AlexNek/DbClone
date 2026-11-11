namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// A group of two or more tables that reach each other by following constraints — the product's
/// own "cyclic group" (§5.5). A group larger than the collapse limit is drawn behind a supernode,
/// and every member stays in the file.
/// </summary>
/// <param name="Number">The group's 1-based number, in the order the register would print it.</param>
/// <param name="Label">The words on the supernode and on the chip: the <c>↺</c> mark and the group's size.</param>
/// <param name="MemberNodeIndexes">The nodes in the group, in canonical order.</param>
/// <param name="IsCollapsed">
/// Whether the group starts collapsed. The members' elements are still emitted, hidden, so
/// expanding is a class toggle and never a second read of the catalog.
/// </param>
internal sealed record CatalogCyclicGroup(
    int Number,
    string Label,
    IReadOnlyList<int> MemberNodeIndexes,
    bool IsCollapsed);
