using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.UI.Models;

namespace DbClone.UI.ViewModels;

/// <summary>
/// ViewModel for the Export Connection dialog.
/// Handles format selection → live preview → clipboard/file output.
/// </summary>
public sealed partial class ExportConnectionViewModel : ObservableObject
{
    private readonly DatabaseConnection _connection;

    private readonly IConnectionExportService _exportService;

    private readonly string? _storedDefaultFormatId;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private EExportOutputMode _outputMode = EExportOutputMode.Clipboard;

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private FormatListItem? _selectedFormat;

    [ObservableProperty]
    private bool _setAsDefault;

    public ObservableCollection<FormatListItem> AvailableFormats { get; } = [];

    public bool CanExport => SelectedFormat is not null;

    /// <summary>Set to true when the user confirms the export (dialog result).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The exported string after confirmation (for the caller to use).</summary>
    public string? ExportedString { get; private set; }

    public bool IsFileMode => OutputMode == EExportOutputMode.File;

    public ExportConnectionViewModel(
        IConnectionExportService exportService,
        DatabaseConnection connection,
        string? defaultFormatId = null)
    {
        _exportService = exportService;
        _connection = connection;
        _storedDefaultFormatId = defaultFormatId;

        // Populate available formats
        var formats = _exportService.GetSupportedFormats(connection);
        foreach (var format in formats)
        {
            AvailableFormats.Add(
                new FormatListItem(format.Id, format.DisplayName, format.TypicalSource));
        }

        // Auto-select the user's default format, fall back to first
        var preferred = AvailableFormats.FirstOrDefault(f => f.Id == defaultFormatId)
                        ?? AvailableFormats.FirstOrDefault();
        if (preferred is not null)
            SelectedFormat = preferred;
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
    }

    [RelayCommand]
    private void Export()
    {
        if (!CanExport) return;

        try
        {
            ExportedString = _exportService.Export(_connection, SelectedFormat!.Id);
            Confirmed = true;
        }
        catch
        {
            Confirmed = false;
        }
    }

    partial void OnOutputModeChanged(EExportOutputMode value)
    {
        OnPropertyChanged(nameof(IsFileMode));
    }

    partial void OnSelectedFormatChanged(FormatListItem? value)
    {
        SetAsDefault = value is not null && value.Id == _storedDefaultFormatId;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (SelectedFormat is null)
        {
            Preview = string.Empty;
            return;
        }

        try
        {
            Preview = _exportService.Export(_connection, SelectedFormat.Id);
        }
        catch (Exception ex)
        {
            Preview = $"Error: {ex.Message}";
        }
    }
}
