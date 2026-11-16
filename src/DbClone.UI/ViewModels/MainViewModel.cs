using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Catalog;
using DbClone.Application.Comparison;
using DbClone.Application.Copy;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Interchange;
using DbClone.Application.Relationship;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;
using DbClone.UI.StoreRestore.Workflows;
using DbClone.UI.StoreRestore.Handlers;
using DbClone.UI.StoreRestore.Services;
using DbClone.UI.StoreRestore.Validators;

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

    private readonly IServerAdminService _serverAdmin;

    private readonly ITableCatalogService _catalogService;

    private readonly IConfirmationService _confirmationService;

    private readonly ITableFilterApplier _filterApplier;

    private readonly ILogger<MainViewModel> _logger;

    private readonly DispatcherTimer? _memoryTimer;

    private readonly ITableSelectionPresetNameValidator _presetNameValidator;

    private readonly ITableSelectionPresetStore _presetStore;

    private readonly ISettingsService _settingsService;

    private readonly ITableSelectionService _tableSelectionService;

    // ── Store/Restore workflow dependencies ──────────────────────────────────────
    private readonly IWorkflowService<StoreRequest, StoreResult> _storeWorkflow;
    private readonly IWorkflowService<RestoreRequest, RestoreResult> _restoreWorkflow;
    private readonly IWorkflowResultHandler _resultHandler;
    private readonly IWorkflowValidator _storeValidator;
    private readonly ICatalogPathResolver _storePathResolver;
    private readonly IDialogService _storeDialogService;
    private readonly IRelationshipViewHandler _relationshipView;
    private readonly ViewModelStateManager _stateManager;
    private CancellationTokenSource? _storeCts;

    /// <summary>The three workflow components behind their common contract.</summary>
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
    /// Store/Restore is not a mode; when it runs, its state is exposed separately via
    /// <see cref="StoreRestoreState"/>.
    /// </summary>
    public WorkflowState ActiveState =>
        Toolbar.SelectedMode switch
        {
            EWorkspaceMode.Compare => Compare.State,
            _ => Copy.State
        };

    public CompareViewModel Compare { get; }

    public ConnectionSectionViewModel Connections { get; }

    public OperationContext Context { get; }

    public CopyOperationViewModel Copy { get; }

    /// <summary>Compare workflow state for XAML bindings (banner, objects strip, status bar).</summary>
    public WorkflowState CompareState => Compare.State;

    /// <summary>Copy workflow state for XAML bindings (banner, objects strip, status bar).</summary>
    public WorkflowState CopyState => Copy.State;

    /// <summary>Store/Restore workflow state for XAML bindings (banner, objects strip, status bar).</summary>
    public WorkflowState StoreRestoreState { get; } = new();

    /// <summary>Store/Restore progress for XAML bindings — the shared progress card binds to it.</summary>
    public StoreRestoreProgressViewModel StoreRestoreProgress { get; }

    [ObservableProperty]
    private bool _isStoreRunning;

    /// <summary>
    /// Whether the store/restore state owns the progress card, the banner, the status bar and the
    /// log pane. Set when a run starts and cleared when another workflow starts or the connection
    /// changes, so a finished run's outcome stays on screen until something replaces it.
    /// </summary>
    [ObservableProperty]
    private bool _isStoreRestoreDisplayed;

    [ObservableProperty]
    private bool _isStorePaused;

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
        IConfirmationService confirmationService,
        IConnectionStore connectionStore,
        IConnectionGroupStore connectionGroupStore,
        ITableCatalogService catalogService,
        IComparisonService comparisonService,
        ISettingsService settingsService,
        IReportExportService reportExportService,
        IConnectionTextService connectionStringService,
        IServerAdminService serverAdmin,
        IConnectionImportService importService,
        IConnectionExportService exportService,
        IUpdateService updateService,
        IServiceProvider serviceProvider,
        ICopyWorkflow copyWorkflow,
        ITableSelectionService tableSelectionService,
        ITableSelectionPresetStore presetStore,
        ITableFilterApplier filterApplier,
        ITableSelectionPresetNameValidator presetNameValidator,
        Application.Interchange.IInterchangeService interchangeService)
    {
        _connectionStore = connectionStore;
        _serverAdmin = serverAdmin;
        _settingsService = settingsService;
        _confirmationService = confirmationService;
        _catalogService = catalogService;
        _tableSelectionService = tableSelectionService;
        _presetStore = presetStore;
        _filterApplier = filterApplier;
        _presetNameValidator = presetNameValidator;
        _logger = serviceProvider.GetRequiredService<ILogger<MainViewModel>>();

        // Load settings
        Settings = settingsService.Load();

        // Settings persistence
        var settingsPersister = new SettingsPersistenceManager(settingsService, Settings);
        settingsPersister.Suspend();

        // Resolve platform definitions (needed by connection VMs and connection manager)
        var platformService = serviceProvider.GetRequiredService<IPlatformService>();

        // Create connection VMs
        var source =
            new ConnectionViewModel(connectionStringService, serverAdmin, platformService)
                {
                    Label = "Source"
                };
        var destination =
            new ConnectionViewModel(connectionStringService, serverAdmin, platformService)
                {
                    Label = "Destination"
                };

        // Table selection panel — source panel only
        source.TableSelection = new TableSelectionPanelViewModel(
            tableSelectionService,
            confirmationService,
            catalogService,
            source);
        source.TableSelection.EditRequested += (_, _) => OpenTableSelectionDialog();

        // Table overview panel — destination panel only
        destination.TableOverview = new TableOverviewPanelViewModel(catalogService, destination);
        destination.TableOverview.ViewRequested += (_, _) => OpenTableOverviewDialog();

        // Create shared context (must exist before stateManager wiring)
        Context = new OperationContext(source, destination);

        // State manager for operation lifecycle
        var stateManager = new ViewModelStateManager();
        _stateManager = stateManager;

        // The progress card binds one shape for every workflow; store/restore answers it from
        // the product's own reports while its own state keeps the log and the banner.
        StoreRestoreProgress = new StoreRestoreProgressViewModel(StoreRestoreState);

        // Create child ViewModels (Copy/Compare first — Log needs their WorkflowState)
        Compare = new CompareViewModel(
            logger: serviceProvider.GetRequiredService<ILogger<CompareViewModel>>(),
            comparisonService,
            catalogService,
            serviceProvider.GetRequiredService<IDialogService>(),
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
            copyWorkflow,
            settingsPersister,
            stateManager,
            tableSelectionService,
            Settings,
            Context);

        // Store/Restore workflow dependencies
        _storeWorkflow = serviceProvider.GetRequiredService<IWorkflowService<StoreRequest, StoreResult>>();
        _restoreWorkflow = serviceProvider.GetRequiredService<IWorkflowService<RestoreRequest, RestoreResult>>();
        _resultHandler = serviceProvider.GetRequiredService<IWorkflowResultHandler>();
        _storeValidator = serviceProvider.GetRequiredService<IWorkflowValidator>();
        _storePathResolver = serviceProvider.GetRequiredService<ICatalogPathResolver>();
        _storeDialogService = serviceProvider.GetRequiredService<IDialogService>();
        _relationshipView = serviceProvider.GetRequiredService<IRelationshipViewHandler>();

        // The busy lock gates the two relationship buttons as it gates every other command (D7).
        Context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OperationContext.IsBusy)) return;

            ViewSourceRelationshipsCommand.NotifyCanExecuteChanged();
            ViewTargetRelationshipsCommand.NotifyCanExecuteChanged();
        };

        _workflows = [Copy, Compare];

        // Route elapsed time to the currently running workflow (symmetric via IWorkflowViewModel)
        stateManager.ElapsedTimeUpdated += elapsed =>
        {
            var running = _workflows.FirstOrDefault(w => w.IsRunning);
            if (running is not null)
                running.State.ElapsedTime = elapsed;
        };

        // Store/Restore elapsed time is routed separately (not a mode-bound workflow)
        stateManager.ElapsedTimeUpdated += elapsed =>
        {
            if (IsStoreRunning)
                StoreRestoreState.ElapsedTime = elapsed;
        };

        Log = new LogPaneViewModel(
            new Dictionary<EWorkspaceMode, WorkflowState>
            {
                [EWorkspaceMode.Copy] = Copy.State,
                [EWorkspaceMode.Compare] = Compare.State
            },
            StoreRestoreState,
            serviceProvider.GetRequiredService<IFileService>());

        Connections = new ConnectionSectionViewModel(
            connectionGroupStore,
            connectionStore,
            connectionStringService,
            serverAdmin,
            importService,
            exportService,
            settingsService,
            backupEncryptionService,
            platformService,
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
            serverAdmin,
            importService,
            exportService,
            settingsService,
            backupEncryptionService,
            catalogService,
            updateService,
            platformService,
            serviceProvider.GetRequiredService<IDialogService>(),
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
        RunAndLogFailures(source.TableSelection.LoadForConnectionAsync(source.SelectedSavedConnection), "LoadForConnectionAsync (source initial)");
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.SelectedSavedConnection))
                RunAndLogFailures(source.TableSelection!.LoadForConnectionAsync(source.SelectedSavedConnection), "LoadForConnectionAsync (source changed)");
        };

        // Restore the destination table overview for the initial connection,
        // then follow destination connection switches.
        RunAndLogFailures(destination.TableOverview.LoadForConnectionAsync(destination.SelectedSavedConnection), "LoadForConnectionAsync (destination initial)");
        destination.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.SelectedSavedConnection))
                RunAndLogFailures(destination.TableOverview!.LoadForConnectionAsync(destination.SelectedSavedConnection), "LoadForConnectionAsync (destination changed)");
        };

        // Update banner — self-contained component with its own ViewModel
        Update = new UpdateInfoBarViewModel(updateService);

        // Auto-switch toolbar mode when an operation starts (e.g. Ctrl+Shift+C from Copy mode)
        Compare.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Compare.IsCompareRunning) && Compare.IsCompareRunning)
            {
                IsStoreRestoreDisplayed = false;
                Toolbar.SelectedMode = EWorkspaceMode.Compare;
            }
        };
        Copy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Copy.IsCopyRunning) && Copy.IsCopyRunning)
            {
                IsStoreRestoreDisplayed = false;
                Toolbar.SelectedMode = EWorkspaceMode.Copy;
            }
        };

        settingsPersister.Resume();
    }

    // ── Store/Restore commands ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartStoreAsync()
    {
        var settingsPersister = new SettingsPersistenceManager(_settingsService, Settings);
        settingsPersister.SaveNow();

        if (!_storeValidator.ValidateSourceConnection(Context.Source, StoreRestoreState)) return;

        var catalogPath = _storePathResolver.PromptForStoreCatalogPath(_storeDialogService, Settings, CatalogNameSource.FromViewModel(Context.Source));
        if (catalogPath is null) return;

        if (!_storeValidator.EnsureDirectoryExists(catalogPath, StoreRestoreState)) return;

        await ExecuteStoreWorkflowAsync("Store", ct => RunStoreAsync(catalogPath, ct));
    }

    [RelayCommand]
    private async Task StartRestoreAsync()
    {
        var settingsPersister = new SettingsPersistenceManager(_settingsService, Settings);
        settingsPersister.SaveNow();

        if (!_storeValidator.ValidateDestinationConnection(Context.Destination, StoreRestoreState)) return;

        var catalogPath = _storePathResolver.PromptForRestoreCatalogPath(_storeDialogService, Settings);
        if (catalogPath is null) return;

        if (!_storeValidator.ValidateDirectoryExists(catalogPath, StoreRestoreState)) return;

        await ExecuteStoreWorkflowAsync("Restore", ct => RunRestoreAsync(catalogPath, ct));
    }

    // ── Relationship view ───────────────────────────────────────────────────────

    /// <summary>Opens the relationship view of the source database, read live.</summary>
    [RelayCommand(CanExecute = nameof(CanViewSourceRelationships))]
    private Task ViewSourceRelationshipsAsync() =>
        ShowDatabaseRelationshipsAsync(Context.Source, ERelationshipRole.Source);

    private bool CanViewSourceRelationships() => CanViewRelationships(Context.Source);

    /// <summary>Opens the relationship view of the destination database, read live.</summary>
    [RelayCommand(CanExecute = nameof(CanViewTargetRelationships))]
    private Task ViewTargetRelationshipsAsync() =>
        ShowDatabaseRelationshipsAsync(Context.Destination, ERelationshipRole.Target);

    private bool CanViewTargetRelationships() => CanViewRelationships(Context.Destination);

    /// <summary>Opens the relationship view of any stored catalog the user picks.</summary>
    [RelayCommand]
    private async Task BrowseRelationshipsAsync()
    {
        var catalogPath = _storePathResolver.PromptForRestoreCatalogPath(_storeDialogService, Settings);
        if (catalogPath is null) return;

        if (!_storeValidator.ValidateDirectoryExists(catalogPath, StoreRestoreState)) return;

        await _relationshipView.ShowCatalogAsync(catalogPath, StoreRestoreState);
    }

    /// <summary>A side is viewable when it names a database and nothing else is running.</summary>
    /// <param name="connection">The side whose button is being asked about.</param>
    /// <returns>Whether its relationship view can be opened now.</returns>
    private bool CanViewRelationships(ConnectionViewModel connection) =>
        !Context.IsBusy && !string.IsNullOrWhiteSpace(connection.DatabaseName);

    /// <summary>
    /// Reads one side and presents it. The busy lock covers the app — a metadata read is a real query —
    /// and no file is written: the document is held in memory (D1).
    /// </summary>
    /// <param name="connection">The side to read.</param>
    /// <param name="role">Which side it is, as the log states it.</param>
    private async Task ShowDatabaseRelationshipsAsync(ConnectionViewModel connection, ERelationshipRole role)
    {
        Context.IsBusy = true;

        try
        {
            var info = ConnectionInfoFactory.FromViewModel(connection);
            await _relationshipView.ShowDatabaseAsync(info, role, StoreRestoreState);
        }
        finally
        {
            Context.IsBusy = false;
        }
    }

    private async Task ExecuteStoreWorkflowAsync(string operationName, Func<CancellationToken, Task> executor)
    {
        _storeCts = new CancellationTokenSource();
        Context.IsBusy = true;
        IsStoreRunning = true;
        IsStoreRestoreDisplayed = true;
        StoreRestoreState.BeginNewRun();
        StoreRestoreProgress.Reset();
        _stateManager.BeginOperation();

        var finalState = EOperationState.Completed;

        try
        {
            await executor(_storeCts.Token);
        }
        catch (OperationCanceledException)
        {
            finalState = EOperationState.Cancelled;
            _resultHandler.HandleCancellation(StoreRestoreState, operationName);
            StoreRestoreProgress.Finish("Cancelled", $"{operationName} cancelled.");
        }
        catch (Exception ex)
        {
            finalState = EOperationState.Failed;
            _logger.LogError(ex, "{Operation} failed", operationName);
            _resultHandler.HandleError(StoreRestoreState, operationName, ex.Message);
            StoreRestoreProgress.Finish("Failed", $"{operationName} failed: {ex.Message}");
        }
        finally
        {
            _stateManager.EndOperation(finalState);
            Context.IsBusy = false;
            IsStoreRunning = false;
            IsStorePaused = false;
            _storeCts?.Dispose();
            _storeCts = null;
        }
    }

    /// <summary>
    /// Hands the run's progress to the card while its state owns the display, and puts the log
    /// pane back on the selected mode's log when it no longer does.
    /// </summary>
    partial void OnIsStoreRestoreDisplayedChanged(bool value) =>
        Log.SetActiveStoreRestore(value);

    /// <summary>Reports one stage or object advance of a running store/restore on the card.</summary>
    private IProgress<InterchangeProgressView> StoreRestoreReporter() =>
        new Progress<InterchangeProgressView>(StoreRestoreProgress.Report);

    private async Task RunStoreAsync(string catalogPath, CancellationToken ct)
    {
        var options = Settings.StoreRestore;
        var connection = ConnectionInfoFactory.FromViewModel(Context.Source);

        var request = new StoreRequest(
            Connection: connection,
            CatalogPath: catalogPath,
            ChunkBytes: options.ChunkBytes,
            RowsPerChunk: options.RowsPerChunk,
            IncludeData: options.IncludeData,
            SnapshotConsistency: options.SnapshotConsistency,
            UnsupportedFeaturePolicy: options.UnsupportedFeaturePolicy,
            TableSelection: _tableSelectionService.OperationSpec,
            IncludeSummaryReport: options.WriteSummaryMarkdown,
            IncludeSqlScripts: options.SqlScripts,
            SqlScriptsMaxFileMb: options.SqlScriptsMaxFileMb,
            Progress: StoreRestoreReporter());

        var result = await _storeWorkflow.ExecuteAsync(request, ct);
        _resultHandler.HandleStoreResult(StoreRestoreState, result);
        StoreRestoreProgress.Finish(result.Success ? "Complete" : "Failed", result.Message);

        // FR17: a catalog that finished arrives with its picture beside it, so the file is there before
        // anybody asks for it. A picture that cannot be written leaves the store a success and says why.
        if (result.Success && !string.IsNullOrWhiteSpace(result.CatalogPath))
        {
            await _relationshipView.TryWriteAsync(result.CatalogPath, StoreRestoreState, ct).ConfigureAwait(false);
        }
    }

    private async Task RunRestoreAsync(string catalogPath, CancellationToken ct)
    {
        var connection = ConnectionInfoFactory.FromViewModel(Context.Destination);

        var request = new RestoreRequest(
            Connection: connection,
            CatalogPath: catalogPath,
            UnsupportedFeaturePolicy: Settings.StoreRestore.UnsupportedFeaturePolicy,
            Progress: StoreRestoreReporter());

        var result = await _restoreWorkflow.ExecuteAsync(request, ct);
        _resultHandler.HandleRestoreResult(StoreRestoreState, result);
        StoreRestoreProgress.Finish(RestorePhase(result), result.Message);
    }

    /// <summary>
    /// The ending a restore reached, in the words the status bar beside the card already uses.
    /// </summary>
    /// <remarks>
    /// A refused restore is neither a success nor a failure — it stops because the target is not empty —
    /// so it is given its own ending instead of being drawn as a failure the reader would go looking for
    /// a cause of.
    /// </remarks>
    private static string RestorePhase(RestoreResult result) =>
        result.WasCancelled ? "Cancelled"
        : result.WasRefused ? "Refused"
        : result.Success ? "Complete"
        : "Failed";

    // ── Private helpers ─────────────────────────────────────────────────────────

    private void RunAndLogFailures(Task task, string operation)
    {
        task.ContinueWith(
            t => _logger.LogError(t.Exception!.InnerException ?? t.Exception, "{Operation} failed", operation),
            TaskContinuationOptions.OnlyOnFaulted);
    }

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

            // A side that newly names a database becomes viewable without leaving the panel (D7).
            ViewSourceRelationshipsCommand.NotifyCanExecuteChanged();
            ViewTargetRelationshipsCommand.NotifyCanExecuteChanged();

            // The previous run's error/result referred to the old connection —
            // drop the visible error display (never while an operation is running)
            if (!Context.IsBusy)
            {
                IsStoreRestoreDisplayed = false;
                Copy.State.ClearResultDisplay();
                Compare.State.ClearResultDisplay();
                StoreRestoreState.ClearResultDisplay();
            }
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
            _confirmationService,
            _catalogService,
            _filterApplier,
            _presetNameValidator,
            Context.Source);

        var dialog = new Views.TableSelectionDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    /// <summary>Opens the read-only table overview dialog for the current destination connection.</summary>
    private void OpenTableOverviewDialog()
    {
        var vm = new TableOverviewViewModel(_serverAdmin, _catalogService, Context.Destination);

        var dialog = new Views.TableOverviewDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();

        // If the user selected a different database inside the dialog, refresh the panel.
        if (dialog.DatabaseChanged)
        {
            Context.Destination.TableOverview?.RefreshAfterDatabaseChange();
        }
    }

    [RelayCommand]
    private void PauseActiveOperation()
    {
        if (IsStoreRunning)
        {
            IsStorePaused = !IsStorePaused;
            StoreRestoreState.Log(IsStorePaused ? "Operation paused" : "Operation resumed");
            return;
        }
        _workflows.FirstOrDefault(w => w.IsRunning)?.PauseCommand.Execute(null);
    }

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
    private void StopActiveOperation()
    {
        if (IsStoreRunning && _storeCts is { IsCancellationRequested: false })
        {
            StoreRestoreState.Log("Stop requested — cancelling operation...");
            _storeCts.Cancel();
            return;
        }
        _workflows.FirstOrDefault(w => w.IsRunning)?.StopCommand.Execute(null);
    }

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
