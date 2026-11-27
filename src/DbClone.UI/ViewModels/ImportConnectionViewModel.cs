using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using CanonicalModel.DTOs;

namespace DbClone.UI.ViewModels;

/// <summary>
/// ViewModel for the Import Connection dialog.
/// Handles paste → auto-detect → preview → import flow.
/// </summary>
public sealed partial class ImportConnectionViewModel : ObservableObject
{
    public const string NewConnectionOption = "New connection";

    private readonly IConnectionImportService _importService;

    [ObservableProperty]
    private string _detectedFormatDisplay = string.Empty;

    [ObservableProperty]
    private DetectionResult? _detection;

    [ObservableProperty]
    private string _detectionState = "none"; // "success", "warning", "error", "none"

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool _isDetected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private ImportResult? _preview;

    [ObservableProperty]
    private string _selectedImportAs = NewConnectionOption;

    // Whether a format understood the text is all Import asks. A field the text left out
    // already arrived with its default and a warning from the import service, so no missing
    // value may disable the button with nothing to explain why.
    public bool CanImport => IsDetected && Preview?.Success == true;

    /// <summary>Set to true when the user confirms the import (dialog result).</summary>
    public bool Confirmed { get; private set; }

    public ObservableCollection<string> ImportAsOptions { get; } = [NewConnectionOption];

    /// <summary>
    /// Engine-neutral prompt for the paste box. The formats themselves are listed below the
    /// box from the installed adapters, so no example here names a dialect, a port or a scheme.
    /// </summary>
    public string InputPlaceholder =>
        "Paste a connection string — a URL, key=value pairs, or a JDBC string";

    /// <summary>
    /// The formats the installed adapters understand, read from the catalog rather than
    /// repeated here: a hard-coded list would drift the moment a format is added.
    /// </summary>
    public string SupportedFormatsDisplay =>
        "Supported: " + string.Join(
            " • ",
            _importService.GetAllFormats()
                          .Select(f => f.DisplayName)
                          .Distinct(StringComparer.OrdinalIgnoreCase));

    public ImportConnectionViewModel(IConnectionImportService importService)
    {
        _importService = importService;
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
    }

    [RelayCommand]
    private void DetectFormat()
    {
        RunDetection();
    }

    [RelayCommand]
    private void Import()
    {
        if (!CanImport) return;
        Confirmed = true;
    }

    partial void OnInputTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            IsDetected = false;
            DetectionState = "none";
            Detection = null;
            Preview = null;
            DetectedFormatDisplay = string.Empty;
            return;
        }

        RunDetection();
    }

    private void RunDetection()
    {
        try
        {
            var detection = _importService.Detect(InputText);
            Detection = detection;

            if (!detection.IsDetected)
            {
                IsDetected = false;
                DetectionState = "error";
                DetectedFormatDisplay = "❌ Unrecognized format";
                Preview = null;
                return;
            }

            var result = _importService.Import(InputText);
            Preview = result;

            if (!result.Success)
            {
                IsDetected = false;
                DetectionState = "error";
                DetectedFormatDisplay = $"❌ {detection.FormatDisplayName} — parse failed";
                return;
            }

            var hasWarnings = result.Warnings.Any(w =>
                w.Level == EWarningLevel.Warning || w.Level == EWarningLevel.Error);

            if (hasWarnings)
            {
                IsDetected = true;
                DetectionState = "warning";
                DetectedFormatDisplay = $"⚠️ {detection.FormatDisplayName}";
            }
            else
            {
                IsDetected = true;
                DetectionState = "success";
                DetectedFormatDisplay = $"✅ {detection.FormatDisplayName}";
            }
        }
        catch
        {
            IsDetected = false;
            Detection = null;
            Preview = null;
            DetectionState = "error";
            DetectedFormatDisplay = "❌ Unrecognized format";
        }
    }
}
