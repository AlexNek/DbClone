using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.TableFilter;
using DbClone.UI.ViewModels;

namespace DbClone.UI.Services;

public sealed class DatabaseService : IDatabaseService
{
    private readonly IDatabaseMaintenanceProvider _maintenanceProvider;

    private readonly ITableInfoProvider _tableInfoProvider;

    public string ProviderName => _maintenanceProvider.ProviderName;

    public DatabaseService(
        ITableInfoProvider tableInfoProvider,
        IDatabaseMaintenanceProvider maintenanceProvider)
    {
        _tableInfoProvider = tableInfoProvider;
        _maintenanceProvider = maintenanceProvider;
    }

    public async Task<bool> CheckDestinationHasDataAsync(
        ConnectionViewModel vm,
        CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        return await _maintenanceProvider.HasDataAsync(info, ct);
    }

    public async Task<IReadOnlyList<string>> CheckPermissionsAsync(
        ConnectionViewModel vm,
        EPermissionCheck checks,
        CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        return await _maintenanceProvider.CheckPermissionsAsync(info, checks, ct);
    }

    public async Task<bool> CleanTargetDatabaseAsync(
        ConnectionViewModel vm,
        Action<string> logMessage,
        CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        return await _maintenanceProvider.CleanDatabaseAsync(info, logMessage, ct);
    }

    public async Task<bool> CleanTargetSelectionAsync(
        ConnectionViewModel source,
        ConnectionViewModel destination,
        TableSelectionSpec spec,
        Action<string> logMessage,
        CancellationToken ct)
    {
        // The selected set is resolved from the SOURCE's table list: target-only
        // tables are unselected by definition and must never be touched.
        var sourceTables = await GetTablesAsync(source, ct);
        var selected = sourceTables
            .Select(t => new TableId(t.Schema, t.Name))
            .Where(id => !spec.IsExcluded(id))
            .ToList();

        var info = ConnectionInfoFactory.FromViewModel(destination);
        return await _maintenanceProvider.CleanTablesAsync(info, selected, logMessage, ct);
    }

    public async Task<bool> CreateBackupDatabaseAsync(
        ConnectionViewModel vm,
        string newDbName,
        Action<string> logMessage,
        CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        return await _maintenanceProvider.CreateDatabaseAsync(info, newDbName, logMessage, ct);
    }

    public async Task<List<(string Schema, string Name)>> GetTablesAsync(
        ConnectionViewModel vm,
        CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        return await _tableInfoProvider.GetTablesAsync(info, ct);
    }

    public async Task<IReadOnlyList<TableSizeInfo>> GetTableSizesAsync(
        ConnectionViewModel vm,
        CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        return await _tableInfoProvider.GetTableSizesAsync(info, ct);
    }

    public async Task<DatabaseMetadata> ReadDatabaseMetadataAsync(
        ConnectionViewModel vm,
        CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        var model = await _tableInfoProvider.ReadDatabaseModelAsync(info, ct: ct);

        return new DatabaseMetadata(
            Tables: model.Tables.Count,
            Views: model.Views.Count,
            MaterializedViews: model.MaterializedViews.Count,
            Sequences: model.Sequences.Count,
            Functions: model.Functions.Count,
            Triggers: model.Triggers.Count,
            Enums: model.Enums.Count,
            Domains: model.Domains.Count,
            CompositeTypes: model.CompositeTypes.Count,
            Indexes: model.Tables.Sum(t => t.Indexes.Count(i => !i.IsPrimary)),
            Constraints: model.Tables.Sum(t =>
                t.ForeignKeys.Count + t.CheckConstraints.Count + t.UniqueConstraints.Count));
    }

    public async Task<DatabaseModel> ReadSourceModelAsync(
        ConnectionViewModel vm,
        CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        return await _tableInfoProvider.ReadDatabaseModelAsync(info, ct: ct);
    }

    public async Task<string?> TestConnectionAsync(ConnectionViewModel vm, CancellationToken ct)
    {
        var info = ConnectionInfoFactory.FromViewModel(vm);
        return await _maintenanceProvider.TestConnectionAsync(info, ct);
    }
}
