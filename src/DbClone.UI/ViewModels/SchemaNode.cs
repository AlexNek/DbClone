using CommunityToolkit.Mvvm.ComponentModel;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Schema entry in the left panel of the table selection dialog.
/// Name = null represents the "All Schemas" global node.
/// </summary>
public sealed partial class SchemaNode : ObservableObject
{
    [ObservableProperty]
    private bool? _isChecked;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>Initializes a new instance.</summary>
    public SchemaNode(string? name, int totalCount)
    {
        Name = name;
        TotalCount = totalCount;
    }

    /// <summary>"selected/total" — always reflects the full database, not the search view.</summary>
    public string CountText => $"{SelectedCount}/{TotalCount}";

    public string DisplayName => Name ?? "All Schemas";

    /// <summary>Schema name, or null for the global node.</summary>
    public string? Name { get; }

    public int TotalCount { get; }

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(CountText));
}
