using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Catalog;
using DbClone.UI.Models;
using DbClone.UI.Services;

using Serilog;

namespace DbClone.UI.ViewModels;

/// <summary>
/// View model for the "Tables:" row on the destination connection panel.
/// Shows table count and a "View…" button to open the read-only overview dialog.
/// When no database is set, shows "(no database)" but the user can still
/// click View… — the dialog itself provides database selection.
/// </summary>
public sealed partial class TableOverviewPanelViewModel : ObservableObject
{
    private readonly ITableCatalogService _catalogService;

    private readonly ConnectionViewModel _connection;

    [ObservableProperty]
    private string? _countText;

    [ObservableProperty]
    private bool _hasConnection;

    [ObservableProperty]
    private bool _hasDatabaseName;

    [ObservableProperty]
    private string? _statusNote;

    /// <summary>Raised when the user clicks View… — the host opens the overview dialog.</summary>
    public event EventHandler? ViewRequested;

    /// <summary>Initializes a new instance.</summary>
    public TableOverviewPanelViewModel(
        ITableCatalogService catalogService,
        ConnectionViewModel connection)
    {
        _catalogService = catalogService;
        _connection = connection;
    }

    /// <summary>Opens the table overview dialog.</summary>
    [RelayCommand]
    private void ViewTables() => ViewRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Loads table count for the given connection.
    /// The panel never blocks on the metadata read.
    /// </summary>
    public async Task LoadForConnectionAsync(SavedConnection? connection)
    {
        HasConnection = connection is not null;
        HasDatabaseName = connection is not null && !string.IsNullOrEmpty(connection.DatabaseName);

        if (connection is null)
        {
            CountText = null;
            StatusNote = null;
            return;
        }

        if (!HasDatabaseName)
        {
            CountText = null;
            StatusNote = null;
            return;
        }

        _ = LoadCountsAsync();
    }

    /// <summary>
    /// Called from the dialog after the user selects a database — refreshes
    /// the panel state to reflect the new database name.
    /// </summary>
    public void RefreshAfterDatabaseChange()
    {
        HasDatabaseName = !string.IsNullOrEmpty(_connection.DatabaseName);

        if (HasDatabaseName)
        {
            _ = LoadCountsAsync();
        }
        else
        {
            CountText = null;
        }
    }

    private async Task LoadCountsAsync()
    {
        try
        {
            var info = ConnectionInfoFactory.FromViewModel(_connection);
            var tables = await _catalogService.GetTablesAsync(info, CancellationToken.None);
            CountText = $"{tables.Count}";
            StatusNote = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TableOverviewPanel] Background table count load failed");
            CountText = null;
            StatusNote = "Table count unavailable — connection check failed";
        }
    }
}
