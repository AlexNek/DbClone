using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.Platforms;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Views;

namespace DbClone.UI.ViewModels;

/// <summary>
/// ViewModel for the unified connection manager window.
/// Manages both Connections tab and Groups tab state.
/// </summary>
public sealed partial class UnifiedConnectionManagerViewModel : ObservableObject
{
    // ── Bulk Export / Import All ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions BackupJsonOptions = new()
        {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    /// <summary>Session-scoped memory for the last active tab index.</summary>
    private static int s_lastTabIndex;

    private readonly IBackupEncryptionService _backupEncryptionService;

    private readonly IConnectionStore _connectionStore;

    private readonly IConnectionStringService _connectionStringService;

    private readonly IConnectionExportService _exportService;

    private readonly IConnectionGroupStore _groupStore;

    private readonly IConnectionImportService _importService;

    private readonly IDatabaseMaintenanceProvider _maintenanceProvider;

    private readonly PlatformSchemaResolver _platformResolver;

    private readonly ISettingsService? _settingsService;

    [ObservableProperty]
    private string? _browseDatabasesError;

    [ObservableProperty]
    private string _connectionSearchText;

    private string? _editingConnectionId;

    private string? _editingGroupId;

    /// <summary>Connection id requested at construction; re-asserted after the view loads.</summary>
    private string? _initialConnectionId;

    /// <summary>Group id requested at construction; re-asserted after the view loads.</summary>
    private string? _initialGroupId;

    [ObservableProperty]
    private string _formBackupName;

    [ObservableProperty]
    private string? _formColor;

    [ObservableProperty]
    private string? _formConnectionType;

    [ObservableProperty]
    private string _formDatabaseName;

    [ObservableProperty]
    private string _formHost;

    // ── Connection form fields ──────────────────────────────────────────────────

    [ObservableProperty]
    private string _formName;

    [ObservableProperty]
    private string _formNotes;

    [ObservableProperty]
    private string _formPassword;

    [ObservableProperty]
    private string _formPort;

    [ObservableProperty]
    private string _formSslMode;

    [ObservableProperty]
    private string _formUsername;

    [ObservableProperty]
    private string? _groupFormColor;

    [ObservableProperty]
    private SavedConnection? _groupFormDestinationConnection;

    // ── Group form fields ───────────────────────────────────────────────────────

    [ObservableProperty]
    private string _groupFormName;

    [ObservableProperty]
    private string _groupFormNotes;

    [ObservableProperty]
    private SavedConnection? _groupFormSourceConnection;

    [ObservableProperty]
    private string _groupSearchText;

    [ObservableProperty]
    private string? _groupValidationError;

    [ObservableProperty]
    private bool _isBrowsingDatabases;

    [ObservableProperty]
    private bool _isEditingConnection;

    [ObservableProperty]
    private bool _isEditingGroup;

    [ObservableProperty]
    private string _portValidationError;

    [ObservableProperty]
    private SavedConnection? _selectedConnection;

    [ObservableProperty]
    private string? _selectedDiscoveredDatabase;

    [ObservableProperty]
    private ConnectionGroup? _selectedGroup;

    // ── Tab management ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _testResult;

    /// <summary>All available connections for source/destination dropdowns.</summary>
    public ObservableCollection<SavedConnection> AvailableConnections { get; } = [];

    /// <summary>Platform entries loaded from .platform files (for the type dropdown).</summary>
    public IReadOnlyList<PlatformEntry> ConnectionTypeValues { get; }

    // ── Browse databases ────────────────────────────────────────────────────────

    /// <summary>Databases discovered on the current server.</summary>
    public ObservableCollection<string> DiscoveredDatabases { get; } = [];

    // ── Connections tab — list and search ────────────────────────────────────────

    /// <summary>Filtered and sorted connections for display.</summary>
    public ObservableCollection<SavedConnection> FilteredConnections { get; } = [];

    // ── Groups tab — list and search ────────────────────────────────────────────

    /// <summary>Filtered and sorted groups for display.</summary>
    public ObservableCollection<ConnectionGroup> FilteredGroups { get; } = [];

    // ── Static data ─────────────────────────────────────────────────────────────

    public static string[] SslModeValues { get; } = ["Disable", "Prefer", "Require"];

    public UnifiedConnectionManagerViewModel(
        IConnectionStore connectionStore,
        IConnectionGroupStore groupStore,
        IConnectionStringService connectionStringService,
        IDatabaseMaintenanceProvider maintenanceProvider,
        IConnectionImportService importService,
        IConnectionExportService exportService,
        IBackupEncryptionService backupEncryptionService,
        PlatformSchemaResolver platformResolver,
        int initialTab = 0,
        string? selectGroupId = null,
        string? selectConnectionId = null,
        ISettingsService? settingsService = null)
    {
        _connectionStore = connectionStore;
        _groupStore = groupStore;
        _connectionStringService = connectionStringService;
        _maintenanceProvider = maintenanceProvider;
        _importService = importService;
        _exportService = exportService;
        _settingsService = settingsService;
        _backupEncryptionService = backupEncryptionService;
        _platformResolver = platformResolver;

        // Populate the connection type dropdown from .platform files
        ConnectionTypeValues = _platformResolver.GetAllPlatforms();
        var defaultPlatformId = ConnectionTypeValues.FirstOrDefault()?.Id;

        // Initialize tab: explicit navigation requests take priority over
        // session memory — editing a connection must always land on the
        // Connections tab, editing a group on the Groups tab.
        if (!string.IsNullOrEmpty(selectConnectionId))
            _selectedTabIndex = 0;
        else if (!string.IsNullOrEmpty(selectGroupId))
            _selectedTabIndex = 1;
        else
            _selectedTabIndex = initialTab == 0 ? s_lastTabIndex : initialTab;

        // Set default form values
        _formHost = "localhost";
        _formPort = "5432";
        _formUsername = "postgres";
        _formPassword = string.Empty;
        _formSslMode = "Prefer";
        _formConnectionType = defaultPlatformId;
        _formName = string.Empty;
        _formDatabaseName = string.Empty;
        _formBackupName = string.Empty;
        _formNotes = string.Empty;
        _connectionSearchText = string.Empty;
        _testResult = string.Empty;
        _portValidationError = string.Empty;

        // Set default group form values
        _groupSearchText = string.Empty;
        _groupFormName = string.Empty;
        _groupFormNotes = string.Empty;

        // Subscribe to store changes
        _connectionStore.Changed += OnConnectionStoreChanged;
        _groupStore.Changed += OnGroupStoreChanged;

        // Initial load
        RefreshFilteredConnections();
        RefreshAvailableConnections();
        RefreshFilteredGroups();

        // Pre-select a specific connection if requested, otherwise auto-select first
        _initialConnectionId = selectConnectionId;
        if (!string.IsNullOrEmpty(selectConnectionId))
        {
            var target = FilteredConnections.FirstOrDefault(c => c.Id == selectConnectionId);
            if (target != null)
                SelectedConnection = target;
            else if (FilteredConnections.Count > 0)
                SelectedConnection = FilteredConnections[0];
        }
        else if (FilteredConnections.Count > 0)
            SelectedConnection = FilteredConnections[0];

        // Pre-select a specific group if requested
        _initialGroupId = selectGroupId;
        if (!string.IsNullOrEmpty(selectGroupId))
        {
            var target = FilteredGroups.FirstOrDefault(g => g.Id == selectGroupId);
            if (target != null)
                SelectedGroup = target;
        }
        // Auto-select first group if Groups tab is active and nothing selected yet
        else if (_selectedTabIndex == 1 && FilteredGroups.Count > 0)
            SelectedGroup = FilteredGroups[0];
    }

    /// <summary>
    /// Re-asserts the requested pre-selection after the view has loaded.
    /// Tab content is materialized lazily, so the list bindings may not be
    /// active when the constructor runs and the initial selection can be
    /// lost; the window calls this once on Loaded to guarantee it sticks.
    /// </summary>
    public void ApplyInitialSelection()
    {
        if (!string.IsNullOrEmpty(_initialConnectionId))
        {
            var target =
                FilteredConnections.FirstOrDefault(c => c.Id == _initialConnectionId);
            if (target != null && !ReferenceEquals(SelectedConnection, target))
                SelectedConnection = target;
        }

        if (!string.IsNullOrEmpty(_initialGroupId))
        {
            var target = FilteredGroups.FirstOrDefault(g => g.Id == _initialGroupId);
            if (target != null && !ReferenceEquals(SelectedGroup, target))
                SelectedGroup = target;
        }
    }

    /// <summary>
    /// Exports all connections and groups to a JSON backup file.
    /// If a password is provided, the file is AES-256 encrypted.
    /// </summary>
    public void ExportAllToPath(string filePath, string? password = null)
    {
        var connections = _connectionStore.GetAll();
        var groups = _groupStore.GetAll();

        var backup = new ConnectionBackupDto
                         {
                             Version = 1,
                             ExportedAt = DateTime.UtcNow,
                             Connections =
                                 connections.Select(c => new ConnectionBackupItem
                                                             {
                                                                 Id = c.Id,
                                                                 Name = c.Name,
                                                                 Host = c.Host,
                                                                 Port = c.Port,
                                                                 DatabaseName = c.DatabaseName,
                                                                 Username = c.Username,
                                                                 Password = c.Password,
                                                                 SslMode = c.SslMode,
                                                                 ConnectionType = c.ConnectionType,
                                                                 Notes = c.Notes,
                                                                 BackupName = c.BackupName,
                                                                 Folder = c.Folder,
                                                                 Color = c.Color
                                                             }).ToList(),
                             Groups = groups.Select(g => new GroupBackupItem
                                                             {
                                                                 Id = g.Id,
                                                                 Name = g.Name,
                                                                 SourceConnectionId =
                                                                     g.SourceConnectionId,
                                                                 DestinationConnectionId =
                                                                     g.DestinationConnectionId,
                                                                 Notes = g.Notes,
                                                                 Color = g.Color
                                                             }).ToList()
                         };

        var json = JsonSerializer.Serialize(backup, BackupJsonOptions);

        if (!string.IsNullOrEmpty(password))
            _backupEncryptionService.WriteEncrypted(filePath, json, password);
        else
            File.WriteAllText(filePath, json);
    }

    /// <summary>Exports the selected connection to a file chosen via SaveFileDialog.</summary>
    public void ExportSelectedToFile()
    {
        if (SelectedConnection is null) return;

        var exported = ExportSelectedAsDefault();

        var dialog = new Microsoft.Win32.SaveFileDialog
                         {
                             Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                             DefaultExt = ".txt",
                             FileName = $"{SelectedConnection.Name}.txt"
                         };

        if (dialog.ShowDialog() == true)
            File.WriteAllText(dialog.FileName, exported);
    }

    /// <summary>
    /// Imports all connections and groups from a JSON backup file.
    /// Automatically detects encrypted files and decrypts with the provided password.
    /// </summary>
    public (int connections, int groups) ImportAllFromPath(string filePath, string? password = null)
    {
        var json = _backupEncryptionService.ReadBackup(filePath, password);
        var backup = JsonSerializer.Deserialize<ConnectionBackupDto>(json, BackupJsonOptions);

        if (backup?.Connections is null)
            return (0, 0);

        int connCount = 0;
        int groupCount = 0;

        foreach (var item in backup.Connections)
        {
            var conn = new SavedConnection
                           {
                               Id = item.Id,
                               Name = item.Name,
                               Host = item.Host,
                               Port = item.Port,
                               DatabaseName = item.DatabaseName,
                               Username = item.Username,
                               Password = item.Password ?? string.Empty,
                               SslMode = item.SslMode,
                               ConnectionType = item.ConnectionType,
                               Notes = item.Notes,
                               BackupName = item.BackupName,
                               Folder = item.Folder,
                               Color = item.Color
                           };
            _connectionStore.Save(conn);
            connCount++;
        }

        if (backup.Groups is not null)
        {
            foreach (var item in backup.Groups)
            {
                var group = new ConnectionGroup
                                {
                                    Id = item.Id,
                                    Name = item.Name,
                                    SourceConnectionId = item.SourceConnectionId,
                                    DestinationConnectionId = item.DestinationConnectionId,
                                    Notes = item.Notes,
                                    Color = item.Color
                                };
                _groupStore.Save(group);
                groupCount++;
            }
        }

        return (connCount, groupCount);
    }

    /// <summary>Imports a connection from raw text (used by Import from File / Clipboard).</summary>
    public void ImportFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var result = _importService.Import(text);
            if (result.Success && result.Connection is not null)
            {
                var saved = MapToSavedConnection(result.Connection);
                _connectionStore.Save(saved);
                SelectedConnection = FilteredConnections.FirstOrDefault(c => c.Id == saved.Id);
            }
        }
        catch
        {
            // Silently ignore unrecognized content
        }
    }

    /// <summary>
    /// Checks whether a backup file is encrypted.
    /// </summary>
    public bool IsBackupEncrypted(string filePath) =>
        _backupEncryptionService.IsEncrypted(filePath);

    [RelayCommand]
    private async Task BrowseDatabasesAsync(CancellationToken ct)
    {
        BrowseDatabasesError = null;
        DiscoveredDatabases.Clear();
        IsBrowsingDatabases = true;

        try
        {
            var port = int.TryParse(FormPort, out var p) ? p : 5432;
            var sslMode = FormSslMode switch
                {
                    "Require" => ESslMode.Require,
                    "Disable" => ESslMode.Disable,
                    _ => ESslMode.Prefer
                };
            var info = new ConnectionInfo(
                FormHost,
                port,
                string.Empty,
                FormUsername,
                FormPassword,
                sslMode);
            var databases = await _maintenanceProvider.ListDatabasesAsync(info, ct);

            if (databases.Count == 0)
            {
                BrowseDatabasesError = "No databases found or could not connect.";
            }
            else
            {
                foreach (var db in databases)
                    DiscoveredDatabases.Add(db);
            }
        }
        catch (OperationCanceledException)
        {
            IsBrowsingDatabases = false;
        }
        catch (Exception ex)
        {
            BrowseDatabasesError = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Builds a <see cref="DatabaseConnection"/> from the current form values so that
    /// export reflects exactly what the user sees — including a password that has been
    /// typed into the form but not yet saved to the store.
    /// </summary>
    private DatabaseConnection BuildDatabaseConnectionFromForm()
    {
        var sslMode = Enum.TryParse<ESslMode>(FormSslMode, true, out var ssl)
                          ? ssl
                          : ESslMode.Prefer;

        if (!int.TryParse(FormPort, out var port) || port <= 0)
            port = 5432;

        return new DatabaseConnection
                   {
                       Name = FormName.Trim(),
                       Provider = EDatabaseProvider.PostgreSql,
                       Host = FormHost.Trim(),
                       Port = port,
                       Database = FormDatabaseName.Trim(),
                       Username = FormUsername.Trim(),
                       Password = string.IsNullOrEmpty(FormPassword) ? null : FormPassword,
                       SslMode = sslMode
                   };
    }

    [RelayCommand]
    private void CloseBrowseDatabases()
    {
        IsBrowsingDatabases = false;
        DiscoveredDatabases.Clear();
        BrowseDatabasesError = null;
    }

    // ── Backup DTOs ──────────────────────────────────────────────────────────

    private sealed class ConnectionBackupDto
    {
        public List<ConnectionBackupItem> Connections { get; set; } = [];

        public DateTime ExportedAt { get; set; }

        public List<GroupBackupItem> Groups { get; set; } = [];

        public int Version { get; set; }
    }

    private sealed class ConnectionBackupItem
    {
        public string BackupName { get; set; } = string.Empty;

        public string? Color { get; set; }

        public string? ConnectionType { get; set; }

        public string DatabaseName { get; set; } = string.Empty;

        public string Folder { get; set; } = "Local";

        public string Host { get; set; } = "localhost";

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;

        public string? Password { get; set; }

        public string Port { get; set; } = "5432";

        public string SslMode { get; set; } = "Prefer";

        public string Username { get; set; } = "postgres";
    }

    [RelayCommand]
    private void DeleteConnection()
    {
        if (SelectedConnection is null) return;
        _connectionStore.Delete(SelectedConnection.Id);
        NewConnection();
    }

    [RelayCommand]
    private void DeleteGroup()
    {
        if (SelectedGroup is null) return;
        _groupStore.Delete(SelectedGroup.Id);
        NewGroup();
    }

    [RelayCommand]
    private void DuplicateConnection()
    {
        if (SelectedConnection is null) return;

        var name = string.IsNullOrWhiteSpace(FormName) ? string.Empty : $"{FormName} - Copy";

        var clone = new SavedConnection
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Name = name,
                            Host = FormHost.Trim(),
                            Port = FormPort.Trim(),
                            DatabaseName = FormDatabaseName.Trim(),
                            Username = FormUsername.Trim(),
                            Password = FormPassword,
                            SslMode = FormSslMode,
                            ConnectionType = FormConnectionType,
                            Notes = FormNotes.Trim(),
                            BackupName = FormBackupName.Trim(),
                            Color = FormColor
                        };

        // Add to list without persisting — user must click Save to persist
        FilteredConnections.Add(clone);
        SelectedConnection = clone;
    }

    /// <summary>Exports the selected connection using the user's default format.</summary>
    private string ExportSelectedAsDefault()
    {
        var dbConnection = BuildDatabaseConnectionFromForm();
        var formatId = _settingsService?.Load().DefaultClipboardFormatId ?? "pg-npgsql";
        return _exportService.Export(dbConnection, formatId);
    }

    [RelayCommand]
    private void ExportToClipboard()
    {
        if (SelectedConnection is null) return;
        System.Windows.Clipboard.SetText(ExportSelectedAsDefault());
    }

    private sealed class GroupBackupItem
    {
        public string? Color { get; set; }

        public string DestinationConnectionId { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;

        public string SourceConnectionId { get; set; } = string.Empty;
    }

    [RelayCommand]
    private void ImportFromClipboard()
    {
        var text = System.Windows.Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var result = _importService.Import(text);
            if (result.Success && result.Connection is not null)
            {
                var saved = MapToSavedConnection(result.Connection);
                _connectionStore.Save(saved);
                SelectedConnection = FilteredConnections.FirstOrDefault(c => c.Id == saved.Id);
            }
        }
        catch
        {
            // Silently ignore unrecognized clipboard content
        }
    }

    private static bool IsPortValid(string portValue)
    {
        if (string.IsNullOrWhiteSpace(portValue))
            return false;

        if (!int.TryParse(portValue, out var port))
            return false;

        return port >= 1 && port <= 65535;
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private void LoadConnectionIntoForm(SavedConnection c)
    {
        _editingConnectionId = c.Id;
        IsEditingConnection = true;
        FormName = c.Name;
        FormHost = c.Host;
        FormDatabaseName = c.DatabaseName;
        FormUsername = c.Username;
        FormPassword = c.Password;

        // Set ConnectionType via backing field to avoid triggering
        // OnFormConnectionTypeChanged which would overwrite Port and SslMode.
#pragma warning disable MVVMTK0034 // Intentional: bypass generated property to avoid side effect
        _formConnectionType = c.ConnectionType;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(FormConnectionType));

        // Now safely set Port and SslMode without being overwritten.
        FormPort = c.Port;
        FormSslMode = c.SslMode;

        FormNotes = c.Notes;
        FormBackupName = c.BackupName;
        FormColor = c.Color;
        TestResult = string.Empty;
        PortValidationError = string.Empty;
    }

    // ── Groups private helpers ──────────────────────────────────────────────────

    private void LoadGroupIntoForm(ConnectionGroup group)
    {
        _editingGroupId = group.Id;
        IsEditingGroup = true;
        GroupFormName = group.Name;
        GroupFormSourceConnection =
            AvailableConnections.FirstOrDefault(c => c.Id == group.SourceConnectionId);
        GroupFormDestinationConnection =
            AvailableConnections.FirstOrDefault(c => c.Id == group.DestinationConnectionId);
        GroupFormNotes = group.Notes;
        GroupFormColor = group.Color;
        GroupValidationError = null;
    }

    // ── Import/Export mapping helpers ─────────────────────────────────────────

    /// <summary>
    /// Infers the platform stable id from the host using
    /// detection.hostPatterns defined in .platform files.
    /// Returns null when no platform matches (base engine).
    /// </summary>
    private string? DetectConnectionType(string host) =>
        _platformResolver.DetectPlatformId(host);

    private SavedConnection MapToSavedConnection(DatabaseConnection dc)
    {
        return new SavedConnection
                   {
                       Id = Guid.NewGuid().ToString("N"),
                       Name = string.IsNullOrEmpty(dc.Name) ? $"{dc.Host}/{dc.Database}" : dc.Name,
                       Host = dc.Host,
                       Port = dc.Port.ToString(),
                       DatabaseName = dc.Database,
                       Username = dc.Username,
                       Password = dc.Password ?? string.Empty,
                       SslMode = dc.SslMode.ToString(),
                       ConnectionType = DetectConnectionType(dc.Host)
                   };
    }

    // ── Connection commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private void NewConnection()
    {
        _editingConnectionId = null;
        IsEditingConnection = false;
        FormName = string.Empty;
        FormHost = "localhost";
        FormPort = "5432";
        FormDatabaseName = string.Empty;
        FormUsername = "postgres";
        FormPassword = string.Empty;
        FormSslMode = "Prefer";
        FormConnectionType = ConnectionTypeValues.FirstOrDefault()?.Id;
        FormNotes = string.Empty;
        FormBackupName = string.Empty;
        FormColor = null;
        TestResult = string.Empty;
        PortValidationError = string.Empty;
        SelectedConnection = null;
    }

    // ── Group commands ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void NewGroup()
    {
        _editingGroupId = null;
        IsEditingGroup = false;
        GroupFormName = string.Empty;
        GroupFormSourceConnection = null;
        GroupFormDestinationConnection = null;
        GroupFormNotes = string.Empty;
        GroupFormColor = null;
        GroupValidationError = null;
        SelectedGroup = null;
    }

    partial void OnConnectionSearchTextChanged(string value)
    {
        RefreshFilteredConnections();
    }

    private void OnConnectionStoreChanged()
    {
        RefreshFilteredConnections();
        RefreshAvailableConnections();

        // If a connection was deleted, clear group form dropdowns referencing it
        if (GroupFormSourceConnection != null &&
            !AvailableConnections.Any(c => c.Id == GroupFormSourceConnection.Id))
        {
            GroupFormSourceConnection = null;
        }

        if (GroupFormDestinationConnection != null &&
            !AvailableConnections.Any(c => c.Id == GroupFormDestinationConnection.Id))
        {
            GroupFormDestinationConnection = null;
        }
    }

    partial void OnFormConnectionTypeChanged(string? value)
    {
        // Unconditionally overwrite Port and SslMode per requirements 4.3
        var defaults = _platformResolver.GetConnectionDefaults(value);
        FormPort = defaults.Port.ToString();
        FormSslMode = defaults.SslMode;
    }

    partial void OnFormPortChanged(string value)
    {
        ValidatePort(value);
    }

    partial void OnGroupSearchTextChanged(string value)
    {
        RefreshFilteredGroups();
    }

    private void OnGroupStoreChanged()
    {
        RefreshFilteredGroups();
    }

    partial void OnSelectedConnectionChanged(SavedConnection? oldValue, SavedConnection? newValue)
    {
        if (oldValue != null)
            oldValue.IsItemSelected = false;
        if (newValue != null)
        {
            newValue.IsItemSelected = true;
            LoadConnectionIntoForm(newValue);
        }
    }

    partial void OnSelectedDiscoveredDatabaseChanged(string? value)
    {
        if (value != null)
        {
            FormDatabaseName = value;
            IsBrowsingDatabases = false;
        }
    }

    partial void OnSelectedGroupChanged(ConnectionGroup? value)
    {
        if (value != null)
            LoadGroupIntoForm(value);
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        s_lastTabIndex = value;

        // Auto-select first group when Groups tab becomes active
        if (value == 1 && FilteredGroups.Count > 0 && SelectedGroup is null)
            SelectedGroup = FilteredGroups[0];
    }

    [RelayCommand]
    private void OpenExportDialog()
    {
        if (SelectedConnection is null) return;

        var dbConnection = BuildDatabaseConnectionFromForm();
        var defaultFormatId = _settingsService?.Load().DefaultClipboardFormatId;
        var vm = new ExportConnectionViewModel(_exportService, dbConnection, defaultFormatId);

        var dialog = new ExportConnectionDialog(vm)
                         {
                             Owner = System.Windows.Application.Current.MainWindow
                         };

        if (dialog.ShowDialog() == true && vm.Confirmed && vm.ExportedString is not null)
        {
            // Persist the new default format if the user checked the box
            if (vm.SetAsDefault && vm.SelectedFormat is not null && _settingsService is not null)
            {
                var settings = _settingsService.Load();
                settings.DefaultClipboardFormatId = vm.SelectedFormat.Id;
                _settingsService.Save(settings);
            }

            if (vm.OutputMode == EExportOutputMode.Clipboard)
                System.Windows.Clipboard.SetText(vm.ExportedString);
            else if (!string.IsNullOrEmpty(vm.FilePath))
                File.WriteAllText(vm.FilePath, vm.ExportedString);
        }
    }

    // ── Import / Export commands ──────────────────────────────────────────────

    [RelayCommand]
    private void OpenImportDialog()
    {
        var vm = new ImportConnectionViewModel(_importService);

        // Populate "Import as" options with existing connection names
        foreach (var conn in _connectionStore.GetAll())
            vm.ImportAsOptions.Add(conn.Name);

        var dialog = new ImportConnectionDialog(vm)
                         {
                             Owner = System.Windows.Application.Current.MainWindow
                         };

        if (dialog.ShowDialog() == true && vm.Confirmed && vm.Preview?.Connection is not null)
        {
            var imported = vm.Preview.Connection;
            var saved = MapToSavedConnection(imported);

            // If importing as existing connection, find and update
            if (vm.SelectedImportAs != ImportConnectionViewModel.NewConnectionOption)
            {
                var existing = _connectionStore.GetAll()
                    .FirstOrDefault(c => c.Name == vm.SelectedImportAs);
                if (existing != null)
                    saved.Id = existing.Id;
            }

            _connectionStore.Save(saved);
            SelectedConnection = FilteredConnections.FirstOrDefault(c => c.Id == saved.Id);
        }
    }

    [RelayCommand]
    private void PickConnectionColor()
    {
        var vm = new ColorPickerViewModel { SelectedColor = FormColor };
        var dialog = new ColorPickerDialog(vm)
                         {
                             Owner = System.Windows.Application.Current.MainWindow
                         };
        if (dialog.ShowDialog() != true) return;

        FormColor = vm.SelectedColor;

        // INPC on SavedConnection.Color updates the list dot immediately.
        if (SelectedConnection is not null)
            SelectedConnection.Color = FormColor;
    }

    [RelayCommand]
    private void PickGroupColor()
    {
        var vm = new ColorPickerViewModel { SelectedColor = GroupFormColor };
        var dialog = new ColorPickerDialog(vm)
                         {
                             Owner = System.Windows.Application.Current.MainWindow
                         };
        if (dialog.ShowDialog() != true) return;

        GroupFormColor = vm.SelectedColor;

        // INPC on ConnectionGroup.Color updates the list dot immediately.
        if (SelectedGroup is not null)
            SelectedGroup.Color = GroupFormColor;
    }

    private void RefreshAvailableConnections()
    {
        var all = _connectionStore.GetAll();
        AvailableConnections.Clear();
        foreach (var c in all.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            AvailableConnections.Add(c);
    }

    private void RefreshFilteredConnections()
    {
        var all = _connectionStore.GetAll();
        var searchText = ConnectionSearchText ?? string.Empty;

        var filtered = string.IsNullOrEmpty(searchText)
                           ? all.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                           : all.Where(c => c.Name.Contains(
                                   searchText,
                                   StringComparison.OrdinalIgnoreCase))
                               .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        FilteredConnections.Clear();
        foreach (var c in filtered)
            FilteredConnections.Add(c);
    }

    private void RefreshFilteredGroups()
    {
        var all = _groupStore.GetAll();
        var searchText = GroupSearchText ?? string.Empty;

        var filtered = string.IsNullOrEmpty(searchText)
                           ? all.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                           : all.Where(g => g.Name.Contains(
                                   searchText,
                                   StringComparison.OrdinalIgnoreCase))
                               .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase);

        FilteredGroups.Clear();
        foreach (var g in filtered)
            FilteredGroups.Add(g);
    }

    [RelayCommand]
    private void SaveConnection()
    {
        // Validate port before saving
        if (!IsPortValid(FormPort))
        {
            ValidatePort(FormPort);
            return;
        }

        // Auto-generate name if empty
        if (string.IsNullOrWhiteSpace(FormName))
            FormName = $"{FormHost.Trim()}/{FormDatabaseName.Trim()}";

        var conn = new SavedConnection
                       {
                           Id = _editingConnectionId ?? Guid.NewGuid().ToString("N"),
                           Name = FormName.Trim(),
                           Host = FormHost.Trim(),
                           Port = FormPort.Trim(),
                           DatabaseName = FormDatabaseName.Trim(),
                           Username = FormUsername.Trim(),
                           Password = FormPassword,
                           SslMode = FormSslMode,
                           ConnectionType = FormConnectionType,
                           Notes = FormNotes.Trim(),
                           BackupName = FormBackupName.Trim(),
                           Color = FormColor
                       };

        _connectionStore.Save(conn);
        SelectedConnection = FilteredConnections.FirstOrDefault(c => c.Id == conn.Id);
    }

    [RelayCommand]
    private void SaveGroup()
    {
        // Validate: both source and destination required
        if (GroupFormSourceConnection is null || GroupFormDestinationConnection is null)
        {
            GroupValidationError = "Both source and destination connections are required.";
            return;
        }

        GroupValidationError = null;

        // Auto-generate name if empty
        if (string.IsNullOrWhiteSpace(GroupFormName))
        {
            var srcName = GroupFormSourceConnection.Name;
            var dstName = GroupFormDestinationConnection.Name;
            GroupFormName = $"{srcName} → {dstName}";
        }

        var group = new ConnectionGroup
                        {
                            Id = _editingGroupId ?? Guid.NewGuid().ToString("N"),
                            Name = GroupFormName.Trim(),
                            SourceConnectionId = GroupFormSourceConnection.Id,
                            DestinationConnectionId = GroupFormDestinationConnection.Id,
                            Notes = GroupFormNotes.Trim(),
                            Color = GroupFormColor
                        };

        _groupStore.Save(group);
        SelectedGroup = FilteredGroups.FirstOrDefault(g => g.Id == group.Id);
    }

    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken ct)
    {
        TestResult = "Testing...";
        try
        {
            var port = int.TryParse(FormPort, out var p) ? p : 5432;
            var sslMode = FormSslMode switch
                {
                    "Require" => ESslMode.Require,
                    "Disable" => ESslMode.Disable,
                    _ => ESslMode.Prefer
                };
            var info = new ConnectionInfo(
                FormHost,
                port,
                FormDatabaseName,
                FormUsername,
                FormPassword,
                sslMode);
            var version = await _maintenanceProvider.TestConnectionAsync(info, ct);
            TestResult = version != null
                             ? $"OK — {_maintenanceProvider.ProviderName} {version}"
                             : "FAILED";
        }
        catch (OperationCanceledException)
        {
            TestResult = "Connection timed out";
        }
        catch (Exception ex)
        {
            TestResult = $"FAILED: {ex.Message}";
        }
    }

    private void ValidatePort(string portValue)
    {
        if (IsPortValid(portValue))
        {
            PortValidationError = string.Empty;
        }
        else
        {
            PortValidationError = "Port must be between 1 and 65535";
        }
    }
}
