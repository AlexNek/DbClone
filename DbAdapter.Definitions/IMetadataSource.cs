using CanonicalModel.Models;

namespace DbAdapter.Definitions;

/// <summary>
/// The metadata role: what is in the database the adapter was created for.
/// </summary>
public interface IMetadataSource
{
    /// <summary>Schema and name of every user table.</summary>
    Task<List<(string Schema, string Name)>> GetTablesAsync(
        CancellationToken ct);

    /// <summary>
    /// Returns estimated sizes for all user tables in a single batched query.
    /// Instant catalog lookup — no table scan. Used by the table selection dialog.
    /// </summary>
    Task<IReadOnlyList<TableSizeInfo>> GetTableSizesAsync(
        CancellationToken ct);

    /// <summary>
    /// Reads the full database model. When <paramref name="excludePlatformSchemas"/>
    /// is true, platform-managed schemas are excluded (used by comparison).
    /// Default is false — copy/backup gets everything.
    /// </summary>
    Task<DatabaseModel> ReadDatabaseModelAsync(
        bool excludePlatformSchemas = false,
        CancellationToken ct = default);
}
