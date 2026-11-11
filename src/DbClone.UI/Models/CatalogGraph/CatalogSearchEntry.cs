namespace DbClone.UI.Models.CatalogGraph;

/// <summary>
/// One searchable fact about one node. The index is built once, in canonical order, so the browser
/// filters a list it was handed and never derives a match of its own.
/// </summary>
/// <param name="NodeIndex">The node the fact belongs to.</param>
/// <param name="Field">Which field of the node matched.</param>
/// <param name="Text">The searched text, as the catalog captured it.</param>
internal sealed record CatalogSearchEntry(
    int NodeIndex,
    ESearchField Field,
    string Text);
