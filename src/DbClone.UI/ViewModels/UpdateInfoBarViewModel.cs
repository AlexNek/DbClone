using System.Diagnostics;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.UI.Services;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Self-contained ViewModel for the update-available info bar.
/// Owns all update banner state, the install command, and a link to release notes.
/// </summary>
public sealed partial class UpdateInfoBarViewModel : ObservableObject
{
    private readonly IUpdateService _updateService;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string? _changelogUrl;

    /// <summary>True when a changelog link is available for the user to review.</summary>
    public bool HasChangelog => !string.IsNullOrEmpty(ChangelogUrl);

    public UpdateInfoBarViewModel(IUpdateService updateService)
    {
        _updateService = updateService;
        _updateService.UpdateCheckCompleted += OnUpdateCheckCompleted;
    }

    [RelayCommand]
    private void InstallUpdate() => _updateService.InstallUpdate();

    [RelayCommand]
    private void ViewReleaseNotes()
    {
        if (string.IsNullOrEmpty(ChangelogUrl)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = ChangelogUrl,
            UseShellExecute = true
        });
    }

    partial void OnChangelogUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(HasChangelog));
    }

    private void OnUpdateCheckCompleted(object? sender, UpdateCheckCompletedEventArgs e)
    {
        if (e.Result.IsUpdateAvailable)
        {
            IsOpen = true;
            ChangelogUrl = e.Result.ChangelogUrl;
            Message = e.Result.Version is { } version
                          ? $"Version {version} is available."
                          : "A new version is available.";
        }
        else if (e.ReportErrors)
        {
            // Explicit manual check found nothing — clear any stale banner.
            IsOpen = false;
            ChangelogUrl = null;
        }
    }
}
