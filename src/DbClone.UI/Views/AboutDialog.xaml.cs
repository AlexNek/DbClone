using System.Diagnostics;
using System.Windows;

using DbClone.UI.Services;

namespace DbClone.UI.Views;

public partial class AboutDialog : Window
{
    private readonly IUpdateService? _updateService;

    public AboutDialog(IUpdateService? updateService = null)
    {
        InitializeComponent();

        _updateService = updateService;
        AppNameText.Text = AppInfo.ProductName;
        VersionText.Text = $"Version {AppInfo.Version}";
        RepoLinkText.Text = AppInfo.RepositoryUrl;

        if (_updateService is not null)
        {
            _updateService.UpdateCheckCompleted += OnUpdateCheckCompleted;
            ShowLastCheck(_updateService.LastCheck);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_updateService is not null)
        {
            _updateService.UpdateCheckCompleted -= OnUpdateCheckCompleted;
        }

        base.OnClosed(e);
    }

    private void OnUpdateCheckCompleted(object? sender, UpdateCheckCompletedEventArgs e)
    {
        ShowLastCheck(e.Result);
    }

    private void ShowLastCheck(UpdateCheckResult? result)
    {
        if (result is null)
        {
            UpdateStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateStatusText.Visibility = Visibility.Visible;
        if (result.IsUpdateAvailable)
        {
            UpdateStatusText.Text = result.Version is { } version
                                        ? $"A new version ({version}) is available."
                                        : "A new version is available.";
            CheckForUpdatesButton.Visibility = Visibility.Collapsed;
            UpdateNowButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatusText.Text = "You're up to date.";
            CheckForUpdatesButton.Visibility = Visibility.Visible;
            UpdateNowButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Checking for updates…";
        UpdateStatusText.Visibility = Visibility.Visible;
        _updateService?.CheckForUpdates(reportErrors: true);
    }

    private void UpdateNowClick(object sender, RoutedEventArgs e)
    {
        _updateService?.InstallUpdate();
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RepoLink_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AppInfo.RepositoryUrl) { UseShellExecute = true });
    }
}
