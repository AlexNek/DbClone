using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DbClone.Application.Enums;
using DbClone.UI.Models;

using Wpf.Ui.Controls;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Per-workflow UI state (logs, banner, status, objects panel, progress visibility,
/// log pane expansion). Each workflow (Copy, Compare) owns an independent instance —
/// starting one workflow never clears the other's state.
/// </summary>
public sealed partial class WorkflowState : ObservableObject
{
    [ObservableProperty]
    private string _elapsedTime = "00:00";

    [ObservableProperty]
    private bool _hasRun;

    [ObservableProperty]
    private string _lastError = string.Empty;

    /// <summary>Message shown in the main-window notification banner.</summary>
    [ObservableProperty]
    private string _bannerMessage = string.Empty;

    /// <summary>Severity (color) of the main-window notification banner.</summary>
    [ObservableProperty]
    private InfoBarSeverity _bannerSeverity = InfoBarSeverity.Informational;

    /// <summary>Title of the main-window notification banner.</summary>
    [ObservableProperty]
    private string _bannerTitle = string.Empty;

    /// <summary>Whether the main-window notification banner is visible.</summary>
    [ObservableProperty]
    private bool _isBannerOpen;

    /// <summary>Whether this workflow's log pane is expanded (per-workflow layout state).</summary>
    [ObservableProperty]
    private bool _isLogPaneExpanded;

    /// <summary>Whether the errors-only filter is active for this workflow's log.</summary>
    [ObservableProperty]
    private bool _isErrorsOnly;

    [ObservableProperty]
    private string _statusBarSummary = "Ready";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    /// <summary>Structured log entries for this workflow.</summary>
    public ObservableCollection<LogEntry> LogMessages { get; } = [];

    /// <summary>Schema objects panel state for this workflow.</summary>
    public ObjectsPanelViewModel ObjectsPanel { get; } = new();

    /// <summary>
    /// Clears all transient state at the start of a new run of this workflow.
    /// Only affects this instance — the other workflow's state is untouched.
    /// </summary>
    public void BeginNewRun()
    {
        LastError = string.Empty;
        IsBannerOpen = false;
        StatusMessage = string.Empty;
        StatusBarSummary = string.Empty;
        ElapsedTime = "00:00";
        HasRun = true;
        ObjectsPanel.Reset();
        LogMessages.Clear();
    }

    /// <summary>Adds a timestamped informational entry.</summary>
    public void Log(string message) =>
        LogMessages.Add(new LogEntry(ELogLevel.Info, message, DateTime.Now));

    /// <summary>Adds an untimestamped continuation line belonging to the previous entry.</summary>
    public void LogDetail(string message, ELogLevel level = ELogLevel.Info) =>
        LogMessages.Add(new LogEntry(level, $"        {message}"));

    /// <summary>Adds a timestamped error entry.</summary>
    public void LogError(string message) =>
        LogMessages.Add(new LogEntry(ELogLevel.Error, message, DateTime.Now));

    /// <summary>Adds a timestamped hint entry (explanatory info about behavior/configuration).</summary>
    public void LogHint(string message) =>
        LogMessages.Add(new LogEntry(ELogLevel.Hint, message, DateTime.Now));

    /// <summary>Adds a timestamped warning entry.</summary>
    public void LogWarning(string message) =>
        LogMessages.Add(new LogEntry(ELogLevel.Warning, message, DateTime.Now));

    /// <summary>Shows the main-window notification banner (e.g. connection test result).</summary>
    public void ShowBanner(string title, string message, InfoBarSeverity severity)
    {
        BannerTitle = title;
        BannerMessage = message;
        BannerSeverity = severity;
        IsBannerOpen = true;
    }

    /// <summary>Mirrors LastError into the notification banner so errors stay prominent.</summary>
    partial void OnLastErrorChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
            IsBannerOpen = false;
        else
            ShowBanner("Operation Failed", value, InfoBarSeverity.Error);
    }
}
