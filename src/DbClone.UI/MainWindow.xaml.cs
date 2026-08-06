using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.ViewModels;

using Wpf.Ui.Controls;

namespace DbClone.UI;

public partial class MainWindow : FluentWindow
{
    private readonly ISettingsService _settingsService;

    /// <summary>Remembered log splitter position of the Compare workflow.</summary>
    private readonly LogPaneSplitterPosition _compareSplitter = new();

    /// <summary>Remembered log splitter position of the Copy workflow.</summary>
    private readonly LogPaneSplitterPosition _copySplitter = new();

    /// <summary>Splitter position of the currently displayed workflow (derived — never mirrored).</summary>
    private LogPaneSplitterPosition ActiveSplitter =>
        (DataContext as MainViewModel)?.Toolbar.SelectedMode == EWorkspaceMode.Compare
            ? _compareSplitter
            : _copySplitter;

    /// <summary>Workflow state of the currently displayed workflow.</summary>
    private WorkflowState? ActiveState => (DataContext as MainViewModel)?.ActiveState;

    public MainWindow(MainViewModel viewModel, ISettingsService settingsService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settingsService = settingsService;

        Title = AppInfo.FullTitle;
        RestoreWindowPosition();
        RestoreLogPaneState();

        Closing += MainWindow_Closing;
        LogPane.SizeChanged += (_, _) => CaptureLogHeight();
        viewModel.Toolbar.PropertyChanged += Toolbar_PropertyChanged;
        viewModel.Copy.State.PropertyChanged += WorkflowState_PropertyChanged;
        viewModel.Compare.State.PropertyChanged += WorkflowState_PropertyChanged;

        // Defer group restoration so the ComboBox ItemsSource binding
        // is fully resolved before SelectedItem is set.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => { viewModel.Connections.RestoreLastUsedGroup(); });
    }

    /// <summary>
    /// Shows or hides the log pane row for the active workflow (pure apply —
    /// height capture happens continuously via LogPane.SizeChanged).
    /// </summary>
    private void ApplyLogPaneVisual(bool expanded)
    {
        if (expanded)
        {
            LogRow.Height = new GridLength(ActiveSplitter.Height);
            LogRow.MinHeight = 80;
        }
        else
        {
            LogRow.Height = new GridLength(0);
            LogRow.MinHeight = 0;
        }
    }

    /// <summary>Feeds the live log row height to the active workflow's splitter position.</summary>
    private void CaptureLogHeight()
    {
        if (LogRow.Height.IsAbsolute)
            ActiveSplitter.Capture(LogRow.Height.Value);
    }

    private void Toolbar_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolbarViewModel.SelectedMode)) return;

        // Toolbar.SelectedMode is the single source of truth — the outgoing workflow's
        // dragged height was already captured live via LogPane.SizeChanged, so here we
        // only apply the remembered layout of the workflow being entered.
        ApplyLogPaneVisual(ActiveState?.IsLogPaneExpanded == true);
    }

    private void WorkflowState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WorkflowState state) return;

        // Auto-expand the failing workflow's log so the user can see what went wrong
        if (e.PropertyName == nameof(WorkflowState.LastError)
            && !string.IsNullOrEmpty(state.LastError))
        {
            state.IsLogPaneExpanded = true;
            return;
        }

        // Re-apply the pane layout when the active workflow's expansion changes
        if (e.PropertyName == nameof(WorkflowState.IsLogPaneExpanded)
            && ReferenceEquals(state, ActiveState))
        {
            ApplyLogPaneVisual(state.IsLogPaneExpanded);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Use the ViewModel's live Settings instance (single source of truth)
        // to avoid overwriting pending changes with a stale disk copy.
        var settings = vm.Settings;

        if (WindowState == WindowState.Maximized)
        {
            settings.WindowMaximized = true;
            settings.WindowLeft = RestoreBounds.Left;
            settings.WindowTop = RestoreBounds.Top;
            settings.WindowWidth = RestoreBounds.Width;
            settings.WindowHeight = RestoreBounds.Height;
        }
        else
        {
            settings.WindowMaximized = false;
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
        }

        settings.CopyLogPaneExpanded = vm.CopyState.IsLogPaneExpanded;
        settings.CompareLogPaneExpanded = vm.CompareState.IsLogPaneExpanded;
        CaptureLogHeight();
        settings.CopyLogPaneHeight = _copySplitter.Height;
        settings.CompareLogPaneHeight = _compareSplitter.Height;

        _settingsService.Save(settings);
    }

    private void RestoreLogPaneState()
    {
        var settings = _settingsService.Load();
        _copySplitter.Restore(settings.CopyLogPaneHeight);
        _compareSplitter.Restore(settings.CompareLogPaneHeight);
        ApplyLogPaneVisual(ActiveState?.IsLogPaneExpanded == true);
    }

    private void RestoreWindowPosition()
    {
        var settings = _settingsService.Load();
        if (double.IsNaN(settings.WindowLeft)) return;

        Left = settings.WindowLeft;
        Top = settings.WindowTop;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }
}
