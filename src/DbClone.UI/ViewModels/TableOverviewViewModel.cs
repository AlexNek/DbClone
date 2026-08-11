using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.UI.Models;
using DbClone.UI.Services;

using Serilog;

namespace DbClone.UI.ViewModels;

/// <summary>
/// View model of the table overview dialog: read-only display of all tables
/// on the destination database with search, sorting, and database selection.
/// When no database is configured, the user picks one from the dropdown.
/// </summary>
public sealed partial class TableOverviewViewModel : ObservableObject
{
    private readonly IDatabaseMaintenanceProvider _maintenanceProvider;

    private readonly IDatabaseService _dbService;

    private readonly ConnectionViewModel _connection;

    private readonly List<TableOverviewItem> _allItems = [];

    private string _sortColumn = "Schema";

    private bool _sortDescending;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBrowsingDatabases;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SchemaNode? _selectedSchemaNode;

    [ObservableProperty]
    private string? _selectedDatabase;

    [ObservableProperty]
    private string _tablesHeader = "TABLES — All Schemas";

    [ObservableProperty]
    private string _totalCountText = string.Empty;

    [ObservableProperty]
    private bool _hasDatabaseSelected;

    /// <summary>Initializes a new instance. Call <see cref="LoadAsync"/> before showing.</summary>
    public TableOverviewViewModel(
        IDatabaseMaintenanceProvider maintenanceProvider,
        IDatabaseService dbService,
        ConnectionViewModel connection)
    {
        _maintenanceProvider = maintenanceProvider;
        _dbService = dbService;
        _connection = connection;
    }

    /// <summary>Show the Schema column — only in the "All Schemas" view.</summary>
    public bool ShowSchemaColumn => SelectedSchemaNode?.Name is null;

    /// <summary>True when the database was changed by the user in this dialog session.</summary>
    public bool DatabaseChanged { get; private set; }

    /// <summary>Available databases on the server.</summary>
    public ObservableCollection<string> AvailableDatabases { get; } = [];

    public ObservableCollection<SchemaNode> SchemaNodes { get; } = [];

    public ObservableCollection<TableOverviewItem> VisibleTables { get; } = [];

    /// <summary>
    /// Initial load: browses databases and loads tables if a database is already set.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        // Always load the database list for the dropdown
        await BrowseDatabasesInternalAsync(ct);

        // If the connection already has a database, load its tables
        if (!string.IsNullOrEmpty(_connection.DatabaseName))
        {
            HasDatabaseSelected = true;
            SelectedDatabase = _connection.DatabaseName;
            await LoadTablesAsync(ct);
        }
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SortBy(string column)
    {
        if (string.Equals(_sortColumn, column, StringComparison.Ordinal))
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = column == "Size";
        }

        RefreshVisibleTables();
    }

    // ── Change handlers ────────────────────────────────────────────────────────

    partial void OnSearchTextChanged(string value) => RefreshVisibleTables();

    partial void OnSelectedSchemaNodeChanged(SchemaNode? value)
    {
        TablesHeader = value?.Name is null
            ? "TABLES — All Schemas"
            : $"TABLES — {value.Name}";
        OnPropertyChanged(nameof(ShowSchemaColumn));
        RefreshVisibleTables();
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (value is null) return;
        if (string.Equals(value, _connection.DatabaseName, StringComparison.OrdinalIgnoreCase)
            && HasDatabaseSelected)
        {
            return; // already loaded
        }

        // Apply to connection and reload tables
        _connection.DatabaseName = value;
        HasDatabaseSelected = true;
        DatabaseChanged = true;

        _ = LoadTablesAsync(CancellationToken.None);
    }

    // ── Database browsing ──────────────────────────────────────────────────────

    private async Task BrowseDatabasesInternalAsync(CancellationToken ct)
    {
        IsBrowsingDatabases = true;

        try
        {
            var sslMode = _connection.SslMode switch
            {
                "Require" => ESslMode.Require,
                "Disable" => ESslMode.Disable,
                _ => ESslMode.Prefer
            };

            var info = new ConnectionInfo(
                _connection.Host,
                _connection.PortNumber,
                string.Empty,
                _connection.Username,
                _connection.Password,
                sslMode);

            var databases = await _maintenanceProvider.ListDatabasesAsync(info, ct);

            AvailableDatabases.Clear();
            foreach (var db in databases)
            {
                AvailableDatabases.Add(db);
            }
        }
        catch (OperationCanceledException)
        {
            // intentionally empty
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TableOverviewDialog] Failed to browse databases");
            LoadError = $"Cannot list databases: {ex.Message}";
        }
        finally
        {
            IsBrowsingDatabases = false;
        }
    }

    // ── Table loading ──────────────────────────────────────────────────────────

    private async Task LoadTablesAsync(CancellationToken ct)
    {
        IsLoading = true;
        LoadError = null;
        _allItems.Clear();
        SchemaNodes.Clear();
        VisibleTables.Clear();
        TotalCountText = string.Empty;

        try
        {
            var model = await _dbService.ReadSourceModelAsync(_connection, ct);

            BuildItems(model);
            BuildSchemaNodes(model);
            UpdateCounts();
            RefreshVisibleTables();

            _ = LoadSizesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[TableOverviewDialog] Failed to read database model");
            LoadError = $"Cannot read tables: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Building ───────────────────────────────────────────────────────────────

    private void BuildItems(DatabaseModel model)
    {
        _allItems.Clear();

        foreach (var table in model.Tables)
        {
            var item = new TableOverviewItem(new TableId(table.SchemaName, table.Name));
            _allItems.Add(item);
        }
    }

    private void BuildSchemaNodes(DatabaseModel model)
    {
        SchemaNodes.Clear();

        var schemas = model.Tables
            .GroupBy(t => t.SchemaName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // "All Schemas" node — TotalCount = total table count
        var all = new SchemaNode(null, model.Tables.Count);
        SchemaNodes.Add(all);

        foreach (var schema in schemas)
        {
            var node = new SchemaNode(schema.Key, schema.Count());
            SchemaNodes.Add(node);
        }

        SelectedSchemaNode = SchemaNodes.FirstOrDefault();
    }

    private async Task LoadSizesAsync()
    {
        try
        {
            var sizes = await _dbService.GetTableSizesAsync(_connection, CancellationToken.None);

            foreach (var size in sizes)
            {
                var item = _allItems.FirstOrDefault(i => i.Id == size.Table);
                if (item is not null)
                {
                    item.SizeBytes = size.SizeBytes;
                }
            }

            if (_sortColumn == "Size")
            {
                RefreshVisibleTables();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TableOverviewDialog] Table size load failed");
        }
    }

    // ── Filtering and sorting ──────────────────────────────────────────────────

    private void UpdateCounts()
    {
        TotalCountText = $"Total: {_allItems.Count} tables";
    }

    private void RefreshVisibleTables()
    {
        VisibleTables.Clear();

        var schemaFilter = SelectedSchemaNode?.Name;
        var search = SearchText.Trim();

        IEnumerable<TableOverviewItem> query = _allItems;

        if (schemaFilter is not null)
        {
            query = query.Where(i =>
                string.Equals(i.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (search.Length > 0)
        {
            query = query.Where(i =>
                i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || i.Schema.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        query = _sortColumn switch
        {
            "Name" => _sortDescending
                ? query.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "Size" => _sortDescending
                ? query.OrderByDescending(i => i.SizeBytes ?? -1)
                : query.OrderBy(i => i.SizeBytes is null).ThenBy(i => i.SizeBytes ?? 0),
            _ => _sortDescending
                ? query.OrderByDescending(i => i.Schema, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderBy(i => i.Schema, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var item in query)
        {
            VisibleTables.Add(item);
        }
    }
}
