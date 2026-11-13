using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.UI.Logging;
using DbClone.UI.Models;
using CanonicalModel.Enums;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Owns log display and commands. Displays the active workflow's log —
/// switching modes swaps the visible collection and the expansion state
/// (each workflow keeps its own history and its own pane layout).
/// Store/Restore is not a mode, so its log takes the pane while that operation owns the
/// display and the selected mode's log returns when it no longer does.
/// Exposed as MainViewModel.Log; LogPaneView binds to it.
/// </summary>
public sealed partial class LogPaneViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<EWorkspaceMode, WorkflowState> _states;

    private readonly WorkflowState? _storeRestoreState;

    private EWorkspaceMode _selectedMode = EWorkspaceMode.Copy;

    private bool _isStoreRestoreActive;

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
        IReadOnlyDictionary<EWorkspaceMode, WorkflowState> states,
        WorkflowState? storeRestoreState = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        _states = states;
        _storeRestoreState = storeRestoreState;
        _activeState = states[EWorkspaceMode.Copy];
        FilteredLogMessages = BuildFilteredView();

        foreach (var state in states.Values)
        {
            state.PropertyChanged += OnWorkflowStateChanged;
        }

        if (storeRestoreState is not null)
            storeRestoreState.PropertyChanged += OnWorkflowStateChanged;
    }

    /// <summary>Switches the visible log collection to the given workspace mode.</summary>
    public void SetActiveMode(EWorkspaceMode mode)
    {
        _selectedMode = mode;
        if (!_isStoreRestoreActive)
            SwitchTo(_states[mode]);
    }

    /// <summary>
    /// Takes the pane while a store/restore owns the display, and gives it back to the
    /// selected mode's log when that run no longer owns it.
    /// </summary>
    public void SetActiveStoreRestore(bool active)
    {
        if (_storeRestoreState is null || _isStoreRestoreActive == active)
            return;

        _isStoreRestoreActive = active;
        SwitchTo(active ? _storeRestoreState : _states[_selectedMode]);
    }

    private void SwitchTo(WorkflowState target)
    {
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
