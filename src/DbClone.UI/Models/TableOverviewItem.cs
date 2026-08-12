using CommunityToolkit.Mvvm.ComponentModel;

using DbClone.Application.Models;

namespace DbClone.UI.Models;

/// <summary>
/// Read-only display model for one table row in the table overview dialog.
/// No selection state — the output overview always shows all tables.
/// </summary>
public sealed partial class TableOverviewItem : ObservableObject
{
    [ObservableProperty]
    private long? _sizeBytes;

    /// <summary>Initializes a new instance.</summary>
    public TableOverviewItem(TableId id) => Id = id;

    /// <summary>Structured identity of the table.</summary>
    public TableId Id { get; }

    public string Schema => Id.Schema;

    public string Name => Id.Name;

    /// <summary>Human-readable size; blank until sizes load.</summary>
    public string SizeDisplay => SizeBytes is null ? string.Empty : FormatSize(SizeBytes.Value);

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
