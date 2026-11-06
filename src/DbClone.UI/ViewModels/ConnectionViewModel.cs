using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.Services;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using CanonicalModel.Platforms;

namespace DbClone.UI.ViewModels;

public sealed partial class ConnectionViewModel : ObservableObject
{
    public event EventHandler? NewConnectionRequested;

    public event EventHandler? EditConnectionRequested;

    private readonly IConnectionTextService _connectionStringService;

    private readonly IServerAdminService _serverAdmin;

    private readonly IPlatformService _platformService;

    [ObservableProperty]
    private string _connectionString = string.Empty;

    [ObservableProperty]
    private string? _connectionType;

    [ObservableProperty]
    private string _databaseName = string.Empty;

    [ObservableProperty]
    private string _host = "localhost";

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _port = string.Empty;

    [ObservableProperty]
    private SavedConnection? _selectedSavedConnection;

    [ObservableProperty]
    private string _sslMode = string.Empty;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    /// <summary>Platform entries loaded from .platform files (for the type dropdown).</summary>
    public IReadOnlyList<PlatformEntry> ConnectionTypeValues { get; }

    public int PortNumber =>
        int.TryParse(Port, out var p)
            ? p
            : _platformService.GetConnectionDefaults(ConnectionType).Port;

    public ObservableCollection<SavedConnection> SavedConnections { get; } = [];

    /// <summary>
    /// Table selection panel — set on the source panel only.
    /// Null on the destination panel; the connection view hides the row.
    /// </summary>
    public TableSelectionPanelViewModel? TableSelection { get; set; }

    /// <summary>
    /// Table overview panel — set on the destination panel only.
    /// Null on the source panel; the connection view hides the row.
    /// </summary>
    public TableOverviewPanelViewModel? TableOverview { get; set; }

    /// <summary>The TLS choices the active engine offers, supplied by its adapter (refactors/09 P7).</summary>
    public IReadOnlyList<string> SslModeValues { get; }

    public string Summary =>
        string.IsNullOrEmpty(DatabaseName)
            ? $"{Host}:{Port}"
            : $"{Host}:{Port}/{DatabaseName}";

    public ConnectionViewModel(
        IConnectionTextService connectionStringService,
        IServerAdminService serverAdmin,
        IPlatformService platformService)
    {
        _connectionStringService = connectionStringService;
        _serverAdmin = serverAdmin;
        _platformService = platformService;
        ConnectionTypeValues = platformService.GetAllPlatforms();
        _connectionType = ConnectionTypeValues.FirstOrDefault()?.Id;
        SslModeValues = connectionStringService.SecurityOptions;

        // A brand-new (unsaved) connection starts from the active engine's platform defaults,
        // never from an engine value written in the UI (refactors/09 P5).
        var defaults = platformService.GetConnectionDefaults(_connectionType);
        _port = defaults.Port.ToString();
        _username = defaults.Username;
        _sslMode = defaults.SslMode;
    }

    public string BuildConnectionString()
    {
        var fields = new ConnectionStringFields(
            Host,
            PortNumber,
            DatabaseName,
            Username,
            Password,
            _connectionStringService.ToSslMode(SslMode));
        return _connectionStringService.BuildKeyValue(fields);
    }

    public void RefreshSavedConnections(IEnumerable<SavedConnection> connections)
    {
        var currentId = SelectedSavedConnection?.Id;
        SavedConnections.Clear();
        foreach (var c in connections.OrderBy(c => c.Name))
        {
            SavedConnections.Add(c);
        }

        if (currentId != null)
        {
            var match = SavedConnections.FirstOrDefault(c => c.Id == currentId);
            if (match != null)
            {
                SelectedSavedConnection = match;
            }
        }
    }

    [RelayCommand]
    private void EditConnection() => EditConnectionRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NewConnection() => NewConnectionRequested?.Invoke(this, EventArgs.Empty);

    partial void OnConnectionStringChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        try
        {
            if (_connectionStringService.TryParse(value, out var fields))
            {
                Host = fields.Host;
                Port = fields.Port.ToString();
                DatabaseName = fields.Database;
                Username = fields.Username;
                Password = fields.Password;
                SslMode = fields.SslMode.ToString();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[ConnectionViewModel] Failed to parse connection string");
        }
    }

    partial void OnConnectionTypeChanged(string? value)
    {
        var defaults = _platformService.GetConnectionDefaults(value);
        SslMode = defaults.SslMode;
        Port = defaults.Port.ToString();
    }

    partial void OnDatabaseNameChanged(string value)
    {
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnSelectedSavedConnectionChanged(SavedConnection? value)
    {
        if (value == null) return;

        // Set ConnectionType first — its change handler resets Port/SslMode to
        // preset defaults.  Then apply the saved values so they take precedence.
        ConnectionType = value.ConnectionType;

        Host = value.Host;
        Port = value.Port;
        DatabaseName = value.DatabaseName;
        Username = value.Username;
        Password = value.Password;
        SslMode = value.SslMode;
        ConnectionString = string.Empty;
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken ct)
    {
        TestResult = "Testing...";
        try
        {
            var info = ConnectionInfoFactory.FromViewModel(this);
            var version = await _serverAdmin.TestConnectionAsync(info, ct);
            TestResult = $"OK — {_serverAdmin.GetProviderName(info)} {version}";
        }
        catch (Exception ex)
        {
            TestResult = $"FAILED: {ex.Message}";
        }
    }
}
