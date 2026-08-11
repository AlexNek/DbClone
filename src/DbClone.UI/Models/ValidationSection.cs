namespace DbClone.UI.Models;

/// <summary>
/// One issue group in the selection validation summary shown before Apply:
/// foreign keys, views or partitions affected by the selection.
/// </summary>
public sealed record ValidationSection(string Title, IReadOnlyList<string> Lines);
