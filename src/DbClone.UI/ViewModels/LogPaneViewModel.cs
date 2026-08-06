using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Enums;
using DbClone.UI.Logging;
using DbClone.UI.Models;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Owns log display and commands. Displays the active workflow's log —
/// switching modes swaps the visible collection and the expansion state
/// (each workflow keeps its own history and its own pane layout).
/// Exposed as MainViewModel.Log; LogPaneView binds to it.
/// </summary>
public sealed partial class LogPaneViewModel : ObservableObject
{
    private readonly WorkflowState _compareState;

    private readonly WorkflowState _copyState;

    private WorkflowState _activeState;

    [ObservableProperty]
    private LogEntry? _selectedLogEntry;

    /// <summary>The workflow state currently displayed in the log pane.</summary>
    public WorkflowState ActiveState => _activeState;

    /// <summary>Filtered view of log entries; respects <see cref="IsErrorsOnly"/>.</summary>
    public ICollectionView FilteredLogMessages { get; private set; }

    /// <summary>Whether the log pane is expanded (per-workflow layout state of the active mode).</summary>
    public bool IsExpanded
    {
        get => _activeState.IsLogPaneExpanded;
        set => _activeState.IsLogPaneExpanded = value;
    }

    /// <summary>Whether the errors-only filter is active (per-workflow state of the active mode).</summary>
    public bool IsErrorsOnly
    {
        get => _activeState.IsErrorsOnly;
        set
        {
            if (_activeState.IsErrorsOnly == value) return;
            _activeState.IsErrorsOnly = value;
            OnPropertyChanged(nameof(IsErrorsOnly));
            FilteredLogMessages.Refresh();
        }
    }

    /// <summary>Log entries of the active workflow.</summary>
    public ObservableCollection<LogEntry> LogMessages => _activeState.LogMessages;

    public string LogToggleSymbol => IsExpanded ? "▼" : "▶";

    public LogPaneViewModel(
        WorkflowState copyState,
        WorkflowState compareState)
    {
        _copyState = copyState;
        _compareState = compareState;
        _activeState = copyState;
        FilteredLogMessages = BuildFilteredView();

        _copyState.PropertyChanged += OnWorkflowStateChanged;
        _compareState.PropertyChanged += OnWorkflowStateChanged;
    }

    /// <summary>Switches the visible log collection to the given workspace mode.</summary>
    public void SetActiveMode(EWorkspaceMode mode)
    {
        var target = mode == EWorkspaceMode.Compare ? _compareState : _copyState;
        if (ReferenceEquals(_activeState, target)) return;

        _activeState = target;
        SelectedLogEntry = null;
        FilteredLogMessages = BuildFilteredView();
        OnPropertyChanged(nameof(ActiveState));
        OnPropertyChanged(nameof(LogMessages));
        OnPropertyChanged(nameof(FilteredLogMessages));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsErrorsOnly));
        OnPropertyChanged(nameof(LogToggleSymbol));
    }

    private void OnWorkflowStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activeState)) return;

        if (e.PropertyName == nameof(WorkflowState.IsLogPaneExpanded))
        {
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(LogToggleSymbol));
        }
        else if (e.PropertyName == nameof(WorkflowState.IsErrorsOnly))
        {
            OnPropertyChanged(nameof(IsErrorsOnly));
            FilteredLogMessages.Refresh();
        }
    }

    private ICollectionView BuildFilteredView()
    {
        var view = CollectionViewSource.GetDefaultView(_activeState.LogMessages);
        view.Filter = item =>
            !_activeState.IsErrorsOnly || ((LogEntry)item).Level == ELogLevel.Error;
        return view;
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogMessages.Clear();
    }

    [RelayCommand]
    private void CopyAllLog()
    {
        var visible = FilteredLogMessages.Cast<LogEntry>().ToList();
        if (visible.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, visible.Select(e => e.Display)));
    }

    [RelayCommand]
    private void CopyLogRow()
    {
        if (SelectedLogEntry is not null)
            Clipboard.SetText(SelectedLogEntry.Display);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var logDir = LoggingConfiguration.GetLogDirectory();
        if (logDir is not null && Directory.Exists(logDir))
            System.Diagnostics.Process.Start("explorer.exe", logDir);
    }

    [RelayCommand]
    private void ToggleLogPane()
    {
        IsExpanded = !IsExpanded;
    }
}
