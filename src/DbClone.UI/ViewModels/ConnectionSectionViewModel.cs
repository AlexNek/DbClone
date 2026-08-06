using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Interfaces;
using DbClone.Application.Platforms;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Owns connection group selection and CRUD logic.
/// Exposed as MainViewModel.Connections; ConnectionSectionView binds to it.
/// </summary>
public sealed partial class ConnectionSectionViewModel : ObservableObject
{
    private readonly IBackupEncryptionService _backupEncryptionService;

    private readonly IConnectionGroupStore _connectionGroupStore;

    private readonly IConnectionStore _connectionStore;

    private readonly IConnectionStringService _connectionStringService;

    private readonly OperationContext _ctx;

    private readonly IConnectionExportService _exportService;

    private readonly IConnectionImportService _importService;

    private readonly IDatabaseMaintenanceProvider _maintenanceProvider;

    private readonly PlatformSchemaResolver _platformResolver;

    private readonly SettingsPersistenceManager _settingsPersister;

    private readonly ISettingsService _settingsService;

    private bool _isRestoringGroup;

    [ObservableProperty]
    private ConnectionGroup? _selectedConnectionGroup;

    /// <summary>All saved connection groups for the dropdown.</summary>
    public ObservableCollection<ConnectionGroup> ConnectionGroups { get; } = [];

    public UserSettings Settings { get; }

    public ConnectionSectionViewModel(
        IConnectionGroupStore connectionGroupStore,
        IConnectionStore connectionStore,
        IConnectionStringService connectionStringService,
        IDatabaseMaintenanceProvider maintenanceProvider,
        IConnectionImportService importService,
        IConnectionExportService exportService,
        ISettingsService settingsService,
        IBackupEncryptionService backupEncryptionService,
        PlatformSchemaResolver platformResolver,
        SettingsPersistenceManager settingsPersister,
        UserSettings settings,
        OperationContext ctx)
    {
        _connectionGroupStore = connectionGroupStore;
        _connectionStore = connectionStore;
        _connectionStringService = connectionStringService;
        _maintenanceProvider = maintenanceProvider;
        _importService = importService;
        _exportService = exportService;
        _backupEncryptionService = backupEncryptionService;
        _settingsService = settingsService;
        _platformResolver = platformResolver;
        _settingsPersister = settingsPersister;
        Settings = settings;
        _ctx = ctx;

        _connectionGroupStore.Changed += RefreshConnectionGroups;
        RefreshConnectionGroups();
    }

    /// <summary>
    /// After a source/destination change, find a matching group or clear the selection.
    /// </summary>
    public void ClearGroupIfConnectionMismatch()
    {
        if (_isRestoringGroup) return;

        var srcId = _ctx.Source.SelectedSavedConnection?.Id;
        var dstId = _ctx.Destination.SelectedSavedConnection?.Id;

        if (SelectedConnectionGroup != null
            && SelectedConnectionGroup.SourceConnectionId == srcId
            && SelectedConnectionGroup.DestinationConnectionId == dstId)
        {
            return;
        }

        var match = srcId != null && dstId != null
                        ? ConnectionGroups.FirstOrDefault(g =>
                            g.SourceConnectionId == srcId && g.DestinationConnectionId == dstId)
                        : null;

        SelectedConnectionGroup = match;
    }

    public void RestoreLastUsedGroup()
    {
        if (string.IsNullOrEmpty(Settings.SelectedConnectionGroupId)) return;

        var group =
            ConnectionGroups.FirstOrDefault(g => g.Id == Settings.SelectedConnectionGroupId);
        if (group is null) return;

        _isRestoringGroup = true;
        try
        {
            SelectedConnectionGroup = group;
        }
        finally
        {
            _isRestoringGroup = false;
        }
    }

    [RelayCommand]
    private void CreateGroupFromCurrent()
    {
        var srcConn = _ctx.Source.SelectedSavedConnection;
        var dstConn = _ctx.Destination.SelectedSavedConnection;
        if (srcConn is null || dstConn is null) return;

        var existing = ConnectionGroups.FirstOrDefault(g =>
            g.SourceConnectionId == srcConn.Id && g.DestinationConnectionId == dstConn.Id);
        if (existing != null)
        {
            SelectedConnectionGroup = existing;
            return;
        }

        var group = new ConnectionGroup
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Name = $"{srcConn.Name} \u2192 {dstConn.Name}",
                            SourceConnectionId = srcConn.Id,
                            DestinationConnectionId = dstConn.Id,
                            Notes = string.Empty
                        };

        _connectionGroupStore.Save(group);
        SelectedConnectionGroup = ConnectionGroups.FirstOrDefault(g => g.Id == group.Id);
    }

    [RelayCommand]
    private void EditCurrentGroup()
    {
        var groupId = SelectedConnectionGroup?.Id;
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
            selectGroupId: groupId,
            settingsService: _settingsService);
        var window = new Views.UnifiedConnectionManagerWindow(vm)
                         {
                             Owner = System.Windows.Application.Current.MainWindow
                         };
        window.ShowDialog();
    }

    partial void OnSelectedConnectionGroupChanged(ConnectionGroup? value)
    {
        Settings.SelectedConnectionGroupId = value?.Id;

        if (value == null) return;

        var srcConn =
            _ctx.Source.SavedConnections.FirstOrDefault(c => c.Id == value.SourceConnectionId);
        var dstConn =
            _ctx.Destination.SavedConnections.FirstOrDefault(c =>
                c.Id == value.DestinationConnectionId);

        if (srcConn != null)
            _ctx.Source.SelectedSavedConnection = srcConn;

        if (dstConn != null)
            _ctx.Destination.SelectedSavedConnection = dstConn;
    }

    private void RefreshConnectionGroups()
    {
        var currentId = SelectedConnectionGroup?.Id;
        ConnectionGroups.Clear();
        foreach (var g in _connectionGroupStore.GetAll().OrderBy(g => g.Name))
            ConnectionGroups.Add(g);

        if (currentId != null)
            SelectedConnectionGroup = ConnectionGroups.FirstOrDefault(g => g.Id == currentId);
    }
}
