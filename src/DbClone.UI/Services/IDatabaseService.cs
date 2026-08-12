using DbClone.Application.Enums;
using DbClone.Application.Models;
using DbClone.Application.TableFilter;
using DbClone.UI.ViewModels;

namespace DbClone.UI.Services;

public interface IDatabaseService
{
    string ProviderName { get; }

    Task<bool> CheckDestinationHasDataAsync(ConnectionViewModel vm, CancellationToken ct);

    Task<IReadOnlyList<string>> CheckPermissionsAsync(
        ConnectionViewModel vm,
        EPermissionCheck checks,
        CancellationToken ct);

    Task<bool> CleanTargetDatabaseAsync(
        ConnectionViewModel vm,
        Action<string> logMessage,
        CancellationToken ct);

    /// <summary>
    /// Selection-scoped target clean: resolves the active spec
    /// against the source's table list and drops only the selected tables —
    /// plus their dependent objects — on the destination.
    /// </summary>
    Task<bool> CleanTargetSelectionAsync(
        ConnectionViewModel source,
        ConnectionViewModel destination,
        TableSelectionSpec spec,
        Action<string> logMessage,
        CancellationToken ct);

    Task<bool> CreateBackupDatabaseAsync(
        ConnectionViewModel vm,
        string newDbName,
        Action<string> logMessage,
        CancellationToken ct);

    Task<List<(string Schema, string Name)>> GetTablesAsync(
        ConnectionViewModel vm,
        CancellationToken ct);

    /// <summary>Estimated sizes for all user tables (single batched catalog query).</summary>
    Task<IReadOnlyList<TableSizeInfo>> GetTableSizesAsync(
        ConnectionViewModel vm,
        CancellationToken ct);

    Task<DatabaseMetadata> ReadDatabaseMetadataAsync(ConnectionViewModel vm, CancellationToken ct);

    /// <summary>Reads the full source database model (tables, FKs, views) over a live connection.</summary>
    Task<DatabaseModel> ReadSourceModelAsync(ConnectionViewModel vm, CancellationToken ct);

    Task<string?> TestConnectionAsync(ConnectionViewModel vm, CancellationToken ct);
}

public sealed record DatabaseMetadata(
    int Tables,
    int Views,
    int MaterializedViews,
    int Sequences,
    int Functions,
    int Triggers,
    int Enums,
    int Domains,
    int CompositeTypes,
    int Indexes,
    int Constraints)
{
    /// <summary>
    /// Builds a human-readable summary listing only non-zero object counts.
    /// Example: "76 tables, 3 views, 2 sequences, 56 functions, 4 enums".
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(8);
            if (Tables > 0) parts.Add($"{Tables} tables");
            if (Views > 0) parts.Add($"{Views} views");
            if (MaterializedViews > 0) parts.Add($"{MaterializedViews} materialized views");
            if (Sequences > 0) parts.Add($"{Sequences} sequences");
            if (Functions > 0) parts.Add($"{Functions} functions");
            if (Triggers > 0) parts.Add($"{Triggers} triggers");
            if (Enums > 0) parts.Add($"{Enums} enums");
            if (Domains > 0) parts.Add($"{Domains} domains");
            if (CompositeTypes > 0) parts.Add($"{CompositeTypes} composite types");
            if (Indexes > 0) parts.Add($"{Indexes} indexes");
            if (Constraints > 0) parts.Add($"{Constraints} constraints");
            return parts.Count > 0 ? string.Join(", ", parts) : "no objects";
        }
    }
}
