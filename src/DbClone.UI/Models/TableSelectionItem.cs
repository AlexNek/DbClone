using CommunityToolkit.Mvvm.ComponentModel;

using DbClone.Application.Models;

namespace DbClone.UI.Models;

/// <summary>
/// UI-only model for one table row in the table selection dialog.
/// Kept separate from Application-layer models.
/// </summary>
public sealed partial class TableSelectionItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private bool _isRelated;

    /// <summary>
    /// Inline marker shown next to the table name while another row is highlighted,
    /// e.g. "🔗 (selected)" or "🔗 (excluded)". Empty when the table is unrelated.
    /// </summary>
    [ObservableProperty]
    private string _relationshipIndicator = string.Empty;

    /// <summary>
    /// Row tooltip while a row is highlighted: the full relationship summary on the
    /// highlighted row itself, a one-line direction note on related rows.
    /// </summary>
    [ObservableProperty]
    private string? _relationshipTooltip;

    [ObservableProperty]
    private long? _sizeBytes;

    /// <summary>Initializes a new instance.</summary>
    public TableSelectionItem(TableId id) => Id = id;

    /// <summary>Structured identity of the table.</summary>
    public TableId Id { get; }

    public string Schema => Id.Schema;

    /// <summary>Human-readable size; blank until sizes load (sorts last).</summary>
    public string SizeDisplay => SizeBytes is null ? string.Empty : FormatSize(SizeBytes.Value);

    public string Name => Id.Name;

    partial void OnSizeBytesChanged(long? value) => OnPropertyChanged(nameof(SizeDisplay));

    private static string FormatSize(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
}
