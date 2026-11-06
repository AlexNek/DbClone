using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;
using CanonicalModel.Models;
using CanonicalModel.Enums;

namespace DbClone.Application.Catalog;

/// <summary>
/// The catalog of one database: what tables it holds, how large they are,
/// what metadata they carry, and the destructive preparation of a target
/// before a copy writes to it.
/// </summary>
public interface ITableCatalogService
{
    /// <summary>Human-readable engine name, e.g. "PostgreSQL".</summary>
    string ProviderName { get; }

    /// <summary>Whether the database holds any user data.</summary>
    Task<bool> CheckDestinationHasDataAsync(
        ConnectionInfo connection,
        CancellationToken ct);

    /// <summary>
    /// Checks whether the user has sufficient permissions for the requested operations.
    /// Returns a list of missing permissions/problems. Empty list means all checks passed.
    /// </summary>
    Task<IReadOnlyList<string>> CheckPermissionsAsync(
        ConnectionInfo connection,
        EPermissionCheck checks,
        CancellationToken ct);

    /// <summary>
    /// Drops all user objects from the target database.
    /// Returns false (without dropping anything) if the user lacks ownership
    /// of schemas present in the database.
    /// </summary>
    Task<bool> CleanTargetDatabaseAsync(
        ConnectionInfo connection,
        Action<string> logMessage,
        CancellationToken ct);

    /// <summary>
    /// Selection-scoped target clean: resolves the active spec
    /// against the source's table list and drops only the selected tables —
    /// plus their dependent objects — on the destination.
    /// </summary>
    Task<bool> CleanTargetSelectionAsync(
        ConnectionInfo source,
        ConnectionInfo destination,
        TableSelectionSpec spec,
        Action<string> logMessage,
        CancellationToken ct);

    /// <summary>Creates a new database on the destination server.</summary>
    Task<bool> CreateBackupDatabaseAsync(
        ConnectionInfo connection,
        string newDbName,
        Action<string> logMessage,
        CancellationToken ct);

    /// <summary>Schema and name of every user table.</summary>
    Task<List<(string Schema, string Name)>> GetTablesAsync(
        ConnectionInfo connection,
        CancellationToken ct);

    /// <summary>Estimated sizes for all user tables (single batched catalog query).</summary>
    Task<IReadOnlyList<TableSizeInfo>> GetTableSizesAsync(
        ConnectionInfo connection,
        CancellationToken ct);

    /// <summary>Reads aggregate metadata counts for the connection's database.</summary>
    Task<DatabaseMetadata> ReadDatabaseMetadataAsync(
        ConnectionInfo connection,
        CancellationToken ct);

    /// <summary>Reads the full source database model (tables, FKs, views) over a live connection.</summary>
    Task<DatabaseModel> ReadSourceModelAsync(
        ConnectionInfo connection,
        CancellationToken ct);

    /// <summary>Returns the server version string, or null when the connection fails.</summary>
    Task<string?> TestConnectionAsync(
        ConnectionInfo connection,
        CancellationToken ct);
}
