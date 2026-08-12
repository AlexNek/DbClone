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

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private string _progressText = "";

    /// <summary>True when a changelog link is available for the user to review.</summary>
    public bool HasChangelog => !string.IsNullOrEmpty(ChangelogUrl);

    public UpdateInfoBarViewModel(IUpdateService updateService)
    {
        _updateService = updateService;
        _updateService.UpdateCheckCompleted += OnUpdateCheckCompleted;
        _updateService.InstallProgressChanged += OnInstallProgressChanged;
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        // Installation state is established by the service's synchronous
        // Downloading event (OnInstallProgressChanged) — no eager UI updates here.
        _updateService.InstallUpdate();
    }

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

    [RelayCommand]
    private void DismissError()
    {
        HasError = false;
        ErrorMessage = "";
        // Restore the original "available" message
        if (_updateService.LastCheck is { IsUpdateAvailable: true, Version: { } version })
            Message = $"Version {version} is available.";
        else
            Message = "A new version is available.";
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
        else if (e.ReportErrors && !e.IsError)
        {
            // Confirmed successful check found no update — clear any stale banner.
            // On failure, preserve the existing banner so the user doesn't lose it.
            IsOpen = false;
            ChangelogUrl = null;
        }
    }

    private void OnInstallProgressChanged(object? sender, InstallProgressEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            switch (e.State)
            {
                case InstallProgressState.Downloading:
                    IsDownloading = true;
                    HasError = false;
                    ProgressText = "Starting…";
                    Message = "Downloading update…";
                    break;

                case InstallProgressState.DownloadProgress:
                    IsDownloading = true;
                    HasError = false;
                    ProgressText = $"{e.ProgressPercent}%";
                    Message = $"Downloading update… {e.ProgressPercent}%";
                    break;

                case InstallProgressState.Launching:
                    IsDownloading = true;
                    HasError = false;
                    ProgressText = "Installing…";
                    Message = "Launching installer…";
                    break;

                case InstallProgressState.Failed:
                    IsDownloading = false;
                    HasError = true;
                    ProgressText = "";
                    ErrorMessage = e.ErrorMessage ?? "Update failed. Please try again or download manually.";
                    Message = "Update failed";
                    break;
            }
        });
    }
}
