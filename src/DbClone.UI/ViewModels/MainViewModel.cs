using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Platforms;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Composite ViewModel — composition root for the main window.
/// Constructs and owns all child ViewModels. Keeps only app-level concerns.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const long BytesPerKB = 1024;
    private const long BytesPerMB = 1024 * 1024;

    private readonly IConnectionStore _connectionStore;

    private readonly IDatabaseService _dbService;

    private readonly IDialogService _dialogService;

    private readonly ITableFilterApplier _filterApplier;

    private readonly DispatcherTimer? _memoryTimer;

    private readonly ITableSelectionPresetNameValidator _presetNameValidator;

    private readonly ITableSelectionPresetStore _presetStore;

    private readonly ISettingsService _settingsService;

    private readonly ITableSelectionService _tableSelectionService;

    /// <summary>The two workflow components behind their common contract.</summary>
    private readonly IWorkflowViewModel[] _workflows;

    // ── App-level state ─────────────────────────────────────────────────────────
    [ObservableProperty]
    private EThemeMode _currentThemeMode = EThemeMode.System;

    [ObservableProperty]
    private string _memoryUsage = "";



    // ── Child ViewModels (composite pattern) ────────────────────────────────────
    /// <summary>
    /// Workflow state of the currently selected workspace mode — the single source of
    /// truth for "which workflow is displayed". XAML and the window code-behind consume
    /// this instead of resolving the mode themselves.
    /// </summary>
    public WorkflowState ActiveState =>
        Toolbar.SelectedMode == EWorkspaceMode.Compare ? Compare.State : Copy.State;

    public CompareViewModel Compare { get; }

    public ConnectionSectionViewModel Connections { get; }

    public OperationContext Context { get; }

    public CopyOperationViewModel Copy { get; }

    /// <summary>Compare workflow state for XAML bindings (banner, objects strip, status bar).</summary>
    public WorkflowState CompareState => Compare.State;

    /// <summary>Copy workflow state for XAML bindings (banner, objects strip, status bar).</summary>
    public WorkflowState CopyState => Copy.State;

    public LogPaneViewModel Log { get; }

    /// <summary>The observable settings instance. UI binds directly to Settings.X properties.</summary>
    public UserSettings Settings { get; }

    /// <summary>Icon reflecting the current theme mode.</summary>
    public SymbolRegular ThemeIcon =>
        CurrentThemeMode switch
            {
                EThemeMode.Light => SymbolRegular.WeatherSunny24,
                EThemeMode.Dark => SymbolRegular.WeatherMoon24,
                _ => SymbolRegular.WeatherSunnyHigh24
            };

    public ToolbarViewModel Toolbar { get; }

    public UpdateInfoBarViewModel Update { get; }

    public MainViewModel(
        ICopyEngine copyEngine,
        IDialogService dialogService,
        IConnectionStore connectionStore,
        IConnectionGroupStore connectionGroupStore,
        IDatabaseService dbService,
        IDatabaseComparerService comparerService,
        ISettingsService settingsService,
        ReportExportService reportExportService,
        IConnectionStringService connectionStringService,
        IDatabaseMaintenanceProvider maintenanceProvider,
        IConnectionImportService importService,
        IConnectionExportService exportService,
        IUpdateService updateService,
        IServiceProvider serviceProvider,
        CopyOperationOrchestrator orchestrator,
        ITableSelectionService tableSelectionService,
        ITableSelectionPresetStore presetStore,
        ITableFilterApplier filterApplier,
        ITableSelectionPresetNameValidator presetNameValidator)
    {
        _connectionStore = connectionStore;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _dbService = dbService;
        _tableSelectionService = tableSelectionService;
        _presetStore = presetStore;
        _filterApplier = filterApplier;
        _presetNameValidator = presetNameValidator;

        // Load settings
        Settings = settingsService.Load();

        // Settings persistence
        var settingsPersister = new SettingsPersistenceManager(settingsService, Settings);
        settingsPersister.Suspend();

        // Resolve platform definitions (needed by connection VMs and connection manager)
        var platformResolver = serviceProvider.GetRequiredService<PlatformSchemaResolver>();

        // Create connection VMs
        var source =
            new ConnectionViewModel(connectionStringService, maintenanceProvider, platformResolver)
                {
                    Label = "Source"
                };
        var destination =
            new ConnectionViewModel(connectionStringService, maintenanceProvider, platformResolver)
                {
                    Label = "Destination"
                };

        // Table selection panel — source panel only
        source.TableSelection = new TableSelectionPanelViewModel(
            tableSelectionService,
            dialogService,
            dbService,
            source);
        source.TableSelection.EditRequested += (_, _) => OpenTableSelectionDialog();

        // Create shared context (must exist before stateManager wiring)
        Context = new OperationContext(source, destination);

        // State manager for operation lifecycle
        var stateManager = new ViewModelStateManager();

        // Create child ViewModels (Copy/Compare first — Log needs their WorkflowState)
        Compare = new CompareViewModel(
            logger: serviceProvider.GetRequiredService<ILogger<CompareViewModel>>(),
            comparerService,
            dbService,
            dialogService,
            reportExportService,
            settingsPersister,
            stateManager,
            tableSelectionService,
            Settings,
            Context);

        // Resolve encryption service
        var backupEncryptionService =
            serviceProvider.GetRequiredService<IBackupEncryptionService>();

        Copy = new CopyOperationViewModel(
            logger: serviceProvider.GetRequiredService<ILogger<CopyOperationViewModel>>(),
            copyEngine,
            orchestrator,
            settingsPersister,
            stateManager,
            tableSelectionService,
            Settings,
            Context);

        _workflows = [Copy, Compare];

        // Route elapsed time to the currently running workflow (symmetric via IWorkflowViewModel)
        stateManager.ElapsedTimeUpdated += elapsed =>
        {
            var running = _workflows.FirstOrDefault(w => w.IsRunning);
            if (running is not null)
                running.State.ElapsedTime = elapsed;
        };

        Log = new LogPaneViewModel(Copy.State, Compare.State);

        Connections = new ConnectionSectionViewModel(
            connectionGroupStore,
            connectionStore,
            connectionStringService,
            maintenanceProvider,
            importService,
            exportService,
            settingsService,
            backupEncryptionService,
            platformResolver,
            settingsPersister,
            Settings,
            Context);

        Toolbar = new ToolbarViewModel(
            Copy,
            Compare,
            Log,
            Settings,
            connectionStore,
            connectionGroupStore,
            connectionStringService,
            maintenanceProvider,
            importService,
            exportService,
            settingsService,
            backupEncryptionService,
            dbService,
            updateService,
            platformResolver,
            serviceProvider.GetRequiredService<ILogger<ToolbarViewModel>>(),
            Context);

        // Wire connection events
        source.NewConnectionRequested +=
            (_, _) => Toolbar.OpenConnectionManagerCommand.Execute(null);
        source.EditConnectionRequested += (sender, _) =>
            Toolbar.OpenConnectionManagerCommand.Execute(
                (sender as ConnectionViewModel)?.SelectedSavedConnection?.Id);
        destination.NewConnectionRequested +=
            (_, _) => Toolbar.OpenConnectionManagerCommand.Execute(null);
        destination.EditConnectionRequested += (sender, _) =>
            Toolbar.OpenConnectionManagerCommand.Execute(
                (sender as ConnectionViewModel)?.SelectedSavedConnection?.Id);

        // Switch the displayed workflow when the workspace mode changes
        Toolbar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ToolbarViewModel.SelectedMode)) return;
            OnPropertyChanged(nameof(ActiveState));
            Log.SetActiveMode(Toolbar.SelectedMode);
        };

        RefreshSavedConnectionLists();
        _connectionStore.Changed += RefreshSavedConnectionLists;

        settingsService.ImportLegacy(connectionStore, source, destination);

        // Memory usage timer
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _memoryTimer.Tick += (_, _) => UpdateMemoryUsage();
        _memoryTimer.Start();
        UpdateMemoryUsage();

        // Restore state (before wiring PropertyChanged — restoring the group
        // sets source/dest connections which would trigger ClearGroupIfConnectionMismatch
        // mid-update and clear the group back to null)
        RestoreLastUsedConnections();
        // Group restoration is deferred to MainWindow.Loaded (via Dispatcher)
        // so the ComboBox ItemsSource is fully bound before SelectedItem is set.
        RestoreUIState();
        Log.SetActiveMode(Toolbar.SelectedMode);

        // Wire connection-change events AFTER restore so user edits are tracked
        source.PropertyChanged += OnConnectionPropertyChanged;
        destination.PropertyChanged += OnConnectionPropertyChanged;

        // Restore the last-used table selection preset for the initial source
        // connection, then follow source connection switches.
        _ = source.TableSelection.LoadForConnectionAsync(source.SelectedSavedConnection);
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.SelectedSavedConnection))
                _ = source.TableSelection!.LoadForConnectionAsync(source.SelectedSavedConnection);
        };

        // Update banner — self-contained component with its own ViewModel
        Update = new UpdateInfoBarViewModel(updateService);

        // Auto-switch toolbar mode when an operation starts (e.g. Ctrl+Shift+C from Copy mode)
        Compare.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Compare.IsCompareRunning) && Compare.IsCompareRunning)
                Toolbar.SelectedMode = EWorkspaceMode.Compare;
        };
        Copy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Copy.IsCopyRunning) && Copy.IsCopyRunning)
                Toolbar.SelectedMode = EWorkspaceMode.Copy;
        };

        settingsPersister.Resume();
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private static bool IsConnectionProperty(string? name) =>
        name switch
            {
                nameof(ConnectionViewModel.SelectedSavedConnection) or
                    nameof(ConnectionViewModel.Host) or
                    nameof(ConnectionViewModel.Port) or
                    nameof(ConnectionViewModel.DatabaseName) or
                    nameof(ConnectionViewModel.Username) or
                    nameof(ConnectionViewModel.Password) => true,
                _ => false
            };



    private void OnConnectionPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (IsConnectionProperty(e.PropertyName))
        {
            Connections.ClearGroupIfConnectionMismatch();
            SyncConnectionsToSettings();
        }
    }

    partial void OnCurrentThemeModeChanged(EThemeMode value)
    {
        OnPropertyChanged(nameof(ThemeIcon));
    }

    /// <summary>Opens the table selection dialog for the current source connection.</summary>
    private void OpenTableSelectionDialog()
    {
        var vm = new TableSelectionViewModel(
            _tableSelectionService,
            _presetStore,
            _dialogService,
            _dbService,
            _filterApplier,
            _presetNameValidator,
            Context.Source);

        var dialog = new Views.TableSelectionDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    [RelayCommand]
    private void PauseActiveOperation() =>
        _workflows.FirstOrDefault(w => w.IsRunning)?.PauseCommand.Execute(null);

    private void RefreshSavedConnectionLists()
    {
        var all = _connectionStore.GetAll();
        Context.Source.RefreshSavedConnections(all);
        Context.Destination.RefreshSavedConnections(all);
    }

    private void RestoreLastUsedConnections()
    {
        RestoreSelectedConnection(Context.Source, Settings.Source);
        RestoreSelectedConnection(Context.Destination, Settings.Destination);

        Context.Source.SelectedSavedConnection ??= Context.Source.SavedConnections.FirstOrDefault();
        Context.Destination.SelectedSavedConnection ??=
            Context.Destination.SavedConnections.FirstOrDefault();
    }

    private static void RestoreSelectedConnection(
        ConnectionViewModel panel,
        ConnectionSettings? saved)
    {
        if (panel.SelectedSavedConnection != null || saved == null)
        {
            return;
        }

        panel.SelectedSavedConnection =
            // Prefer stable ID match; fall back to property match for legacy settings
            (!string.IsNullOrEmpty(saved.Id)
                 ? panel.SavedConnections.FirstOrDefault(c => c.Id == saved.Id)
                 : null)
            ?? panel.SavedConnections.FirstOrDefault(c =>
                c.Host == saved.Host &&
                c.Port == saved.Port &&
                c.DatabaseName == saved.DatabaseName)
            ?? panel.SavedConnections.FirstOrDefault();
    }

    private void RestoreUIState()
    {
        CurrentThemeMode = Settings.Theme;
        Toolbar.SelectedMode = Settings.SelectedWorkspaceMode;
        Copy.State.IsLogPaneExpanded = Settings.CopyLogPaneExpanded;
        Compare.State.IsLogPaneExpanded = Settings.CompareLogPaneExpanded;
    }

    [RelayCommand]
    private void StopActiveOperation() =>
        _workflows.FirstOrDefault(w => w.IsRunning)?.StopCommand.Execute(null);

    private void SyncConnectionsToSettings()
    {
        Settings.Source = ToConnectionSettings(Context.Source);
        Settings.Destination = ToConnectionSettings(Context.Destination);
        Settings.SelectedConnectionGroupId = Connections.SelectedConnectionGroup?.Id;
    }

    private static ConnectionSettings ToConnectionSettings(ConnectionViewModel c) =>
        new()
            {
                Id = c.SelectedSavedConnection?.Id,
                Host = c.Host,
                Port = c.Port,
                DatabaseName = c.DatabaseName,
                Username = c.Username
            };

    [RelayCommand]
    private void ToggleTheme()
    {
        CurrentThemeMode = CurrentThemeMode switch
            {
                EThemeMode.System => EThemeMode.Light,
                EThemeMode.Light => EThemeMode.Dark,
                _ => EThemeMode.System
            };

        var theme = CurrentThemeMode switch
            {
                EThemeMode.Light => ApplicationTheme.Light,
                EThemeMode.Dark => ApplicationTheme.Dark,
                _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                         ? ApplicationTheme.Dark
                         : ApplicationTheme.Light
            };
        ApplicationThemeManager.Apply(theme);

        Settings.Theme = CurrentThemeMode;
        _settingsService.Save(Settings);
    }

    private void UpdateMemoryUsage()
    {
        var mem = GC.GetTotalMemory(false);
        MemoryUsage = mem >= BytesPerMB ? $"{mem / (double)BytesPerMB:F1} MB" : $"{mem / (double)BytesPerKB:F0} KB";
    }
}
