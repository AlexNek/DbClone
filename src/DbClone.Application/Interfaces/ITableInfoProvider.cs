using DbClone.Application.DTOs;
using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

public interface ITableInfoProvider
{
    Task<List<(string Schema, string Name)>> GetTablesAsync(
        ConnectionInfo connection,
        CancellationToken ct);

    /// <summary>
    /// Returns estimated sizes for all user tables in a single batched query.
    /// Instant catalog lookup — no table scan. Used by the table selection dialog.
    /// </summary>
    Task<IReadOnlyList<TableSizeInfo>> GetTableSizesAsync(
        ConnectionInfo connection,
        CancellationToken ct);

    /// <summary>
    /// Reads the full database model. When <paramref name="excludePlatformSchemas"/>
    /// is true, platform-managed schemas are excluded (used by comparison).
    /// Default is false — copy/backup gets everything.
    /// </summary>
    Task<DatabaseModel> ReadDatabaseModelAsync(
        ConnectionInfo connection,
        bool excludePlatformSchemas = false,
        CancellationToken ct = default);
}
