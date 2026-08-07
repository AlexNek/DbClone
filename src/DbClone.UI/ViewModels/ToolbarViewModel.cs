using System.Diagnostics;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Platforms;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;

using Microsoft.Extensions.Logging;

using Wpf.Ui.Controls;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Owns toolbar command orchestration.
/// Exposed as MainViewModel.Toolbar; ToolbarView binds to it.
/// </summary>
public sealed partial class ToolbarViewModel : ObservableObject
{
    private readonly IBackupEncryptionService _backupEncryptionService;

    private readonly IConnectionGroupStore _connectionGroupStore;

    private readonly IConnectionStore _connectionStore;

    private readonly IConnectionStringService _connectionStringService;

    private readonly OperationContext _ctx;

    private readonly IDatabaseService _dbService;

    private readonly IConnectionExportService _exportService;

    private readonly IConnectionImportService _importService;

    private readonly ILogger<ToolbarViewModel> _logger;

    private readonly IDatabaseMaintenanceProvider _maintenanceProvider;

    private readonly PlatformSchemaResolver _platformResolver;

    private readonly ISettingsService _settingsService;

    private readonly IUpdateService _updateService;

    /// <summary>Compare VM reference for XAML command bindings.</summary>
    public CompareViewModel Compare { get; }

    /// <summary>Context reference for IsBusy binding.</summary>
    public OperationContext Context => _ctx;

    /// <summary>Copy VM reference for XAML command bindings.</summary>
    public CopyOperationViewModel Copy { get; }

    /// <summary>Log VM reference for XAML command bindings.</summary>
    public LogPaneViewModel Log { get; }

    /// <summary>
    /// Active workspace mode — bound from MainViewModel for toolbar visibility.
    /// The toolbar does not own this; it receives it via binding from the DataContext parent.
    /// </summary>
    [ObservableProperty]
    private EWorkspaceMode _selectedMode = EWorkspaceMode.Copy;

    /// <summary>Settings reference for checkbox bindings.</summary>
    public UserSettings Settings { get; }

    public ToolbarViewModel(
        CopyOperationViewModel copy,
        CompareViewModel compare,
        LogPaneViewModel log,
        UserSettings settings,
        IConnectionStore connectionStore,
        IConnectionGroupStore connectionGroupStore,
        IConnectionStringService connectionStringService,
        IDatabaseMaintenanceProvider maintenanceProvider,
        IConnectionImportService importService,
        IConnectionExportService exportService,
        ISettingsService settingsService,
        IBackupEncryptionService backupEncryptionService,
        IDatabaseService dbService,
        IUpdateService updateService,
        PlatformSchemaResolver platformResolver,
        ILogger<ToolbarViewModel> logger,
        OperationContext ctx)
    {
        Copy = copy;
        Compare = compare;
        Log = log;
        Settings = settings;
        _connectionStore = connectionStore;
        _connectionGroupStore = connectionGroupStore;
        _connectionStringService = connectionStringService;
        _maintenanceProvider = maintenanceProvider;
        _importService = importService;
        _exportService = exportService;
        _settingsService = settingsService;
        _backupEncryptionService = backupEncryptionService;
        _dbService = dbService;
        _updateService = updateService;
        _platformResolver = platformResolver;
        _logger = logger;
        _ctx = ctx;
    }

    partial void OnSelectedModeChanged(EWorkspaceMode value)
    {
        Settings.SelectedWorkspaceMode = value;
    }

    [RelayCommand]
    private void CheckForUpdates()
    {
        _updateService.CheckForUpdates(reportErrors: true);
    }

    [RelayCommand]
    private void ManageConnectionGroups()
    {
        var vm = new UnifiedConnectionManagerViewModel(
            _connectionStore,
            _connectionGroupStore,
            _connectionStringService,
            _maintenanceProvider,
            _importService,
            _exportService,
            _backupEncryptionService,
            _platformResolver,
            initialTab: 1,
            settingsService: _settingsService);
        var window = new Views.UnifiedConnectionManagerWindow(vm)
                         {
                             Owner = System.Windows.Application.Current.MainWindow
                         };
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenConnectionManager(string? selectConnectionId = null)
    {
        var vm = new UnifiedConnectionManagerViewModel(
            _connectionStore,
            _connectionGroupStore,
            _connectionStringService,
            _maintenanceProvider,
            _importService,
            _exportService,
            _backupEncryptionService,
            _platformResolver,
            initialTab: 0,
            selectConnectionId: selectConnectionId,
            settingsService: _settingsService);
        var window = new Views.UnifiedConnectionManagerWindow(vm)
                         {
                             Owner = System.Windows.Application.Current.MainWindow
                         };
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenHelp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.DocumentationUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open documentation URL");
            ActiveState.ShowBanner(
                "Could not open documentation",
                ex.Message,
                InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private void ShowAbout()
    {
        var dialog = new Views.AboutDialog(_updateService)
                         {
                             Owner = System.Windows.Application.Current.MainWindow
                         };
        dialog.ShowDialog();
    }

    /// <summary>The workflow state of the currently selected mode (utility actions log here).</summary>
    private WorkflowState ActiveState =>
        SelectedMode == EWorkspaceMode.Compare ? Compare.State : Copy.State;
    
    [RelayCommand]
    private async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        var state = ActiveState;
        state.LogMessages.Clear();
        state.IsBannerOpen = false;
        state.Log("Testing connections...");
        state.LogDetail(
            $"Source: {_ctx.Source.Host}:{_ctx.Source.PortNumber}/{_ctx.Source.DatabaseName}");
        state.LogDetail(
            $"Destination: {_ctx.Destination.Host}:{_ctx.Destination.PortNumber}/{_ctx.Destination.DatabaseName}");
    
        var sourceOk = false;
        var destOk = false;
        string sourceNote;
        string destNote;
    
        try
        {
            var srcVer = await _dbService.TestConnectionAsync(_ctx.Source, ct);
            sourceOk = srcVer != null;
            sourceNote = sourceOk ? $"OK — {_dbService.ProviderName} {srcVer}" : "FAILED";
            state.LogDetail(
                $"Source connection: {sourceNote}",
                sourceOk ? ELogLevel.Info : ELogLevel.Error);
        }
        catch (Exception ex)
        {
            sourceNote = $"FAILED — {ex.Message}";
            state.LogDetail($"Source connection: {sourceNote}", ELogLevel.Error);
        }
    
        try
        {
            var dstVer = await _dbService.TestConnectionAsync(_ctx.Destination, ct);
            destOk = dstVer != null;
            destNote = destOk ? $"OK — {_dbService.ProviderName} {dstVer}" : "FAILED";
            state.LogDetail(
                $"Destination connection: {destNote}",
                destOk ? ELogLevel.Info : ELogLevel.Error);
        }
        catch (Exception ex)
        {
            destNote = $"FAILED — {ex.Message}";
            state.LogDetail($"Destination connection: {destNote}", ELogLevel.Error);
        }
    
        var allOk = sourceOk && destOk;
        state.LogDetail(
            $"Connection test: {(allOk ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED")}",
            allOk ? ELogLevel.Info : ELogLevel.Error);
        state.StatusBarSummary = allOk ? "Connection test: OK" : "Connection test: FAILED";
        state.ShowBanner(
            allOk ? "Connection test passed" : "Connection test failed",
            $"Source: {sourceNote}\nDestination: {destNote}",
            allOk ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
}
