namespace DbClone.UI.Models;

/// <summary>
/// Non-null ComboBox item for the comparison filter bar.
/// WPF ComboBox cannot select a null item, so "All …" sentinel entries
/// wrap a null <see cref="Value"/> instead of being null themselves.
/// </summary>
/// <param name="Display">Text shown in the dropdown.</param>
/// <param name="Value">Filter value; null means "no filter" (show all).</param>
public sealed record CompareFilterOption<T>(string Display, T Value);
