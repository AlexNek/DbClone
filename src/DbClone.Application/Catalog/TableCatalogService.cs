using DbAdapter.Definitions;
using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;
using CanonicalModel.Models;
using CanonicalModel.Enums;

namespace DbClone.Application.Catalog;

/// <summary>
/// The catalog of one database: what tables it holds, how large they are,
/// what metadata they carry, and the destructive preparation of a target
/// before a copy writes to it. Each method builds the roles it needs from
/// the adapter for the target the given connection describes.
/// </summary>
public sealed class TableCatalogService : ITableCatalogService
{
    private readonly IDatabaseAdapter _adapter;

    private readonly DatabaseTargetFactory _targetFactory;

    /// <inheritdoc />
    public string ProviderName => _adapter.ProviderName;

    /// <summary>Initializes a new instance.</summary>
    public TableCatalogService(
        IDatabaseAdapter adapter,
        DatabaseTargetFactory targetFactory)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));
    }

    /// <inheritdoc />
    public async Task<bool> CheckDestinationHasDataAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var admin = _adapter.CreateAdmin(_targetFactory.Create(connection));
        return await admin.HasDataAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> CheckPermissionsAsync(
        ConnectionInfo connection,
        EPermissionCheck checks,
        CancellationToken ct)
    {
        var cleaner = _adapter.CreateCleaner(_targetFactory.Create(connection));
        return await cleaner.CheckPermissionsAsync(checks, ct);
    }

    /// <inheritdoc />
    public async Task<bool> CleanTargetDatabaseAsync(
        ConnectionInfo connection,
        Action<string> logMessage,
        CancellationToken ct)
    {
        var cleaner = _adapter.CreateCleaner(_targetFactory.Create(connection));
        return await cleaner.CleanDatabaseAsync(logMessage, ct);
    }

    /// <inheritdoc />
    public async Task<bool> CleanTargetSelectionAsync(
        ConnectionInfo source,
        ConnectionInfo destination,
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

        var cleaner = _adapter.CreateCleaner(_targetFactory.Create(destination));
        return await cleaner.CleanTablesAsync(selected, logMessage, ct);
    }

    /// <inheritdoc />
    public async Task<bool> CreateBackupDatabaseAsync(
        ConnectionInfo connection,
        string newDbName,
        Action<string> logMessage,
        CancellationToken ct)
    {
        var admin = _adapter.CreateAdmin(_targetFactory.Create(connection));
        return await admin.CreateDatabaseAsync(newDbName, logMessage, ct);
    }

    /// <inheritdoc />
    public async Task<List<(string Schema, string Name)>> GetTablesAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var metadata = _adapter.CreateMetadataSource(_targetFactory.Create(connection));
        return await metadata.GetTablesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TableSizeInfo>> GetTableSizesAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var metadata = _adapter.CreateMetadataSource(_targetFactory.Create(connection));
        return await metadata.GetTableSizesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<DatabaseMetadata> ReadDatabaseMetadataAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var metadata = _adapter.CreateMetadataSource(_targetFactory.Create(connection));
        var model = await metadata.ReadDatabaseModelAsync(ct: ct);

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

    /// <inheritdoc />
    public async Task<DatabaseModel> ReadSourceModelAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var metadata = _adapter.CreateMetadataSource(_targetFactory.Create(connection));
        return await metadata.ReadDatabaseModelAsync(ct: ct);
    }

    /// <inheritdoc />
    public async Task<string?> TestConnectionAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var admin = _adapter.CreateAdmin(_targetFactory.Create(connection));
        return await admin.TestConnectionAsync(ct);
    }
}
