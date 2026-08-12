using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

public interface IDatabaseMaintenanceProvider
{
    string ProviderName { get; }

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
    /// of schemas present in the database — the caller must abort the copy.
    /// </summary>
    Task<bool> CleanDatabaseAsync(
        ConnectionInfo connection,
        Action<string> logMessage,
        CancellationToken ct);

    /// <summary>
    /// Selection-scoped clean: drops only the listed tables —
    /// together with the objects that depend on them — from the target database.
    /// Tables outside the list are never modified. When a listed table cannot be
    /// dropped without touching an unlisted table (a foreign key from an unlisted
    /// table, or a partition boundary crossing the list), the clean aborts
    /// before any destructive change and returns false.
    /// </summary>
    Task<bool> CleanTablesAsync(
        ConnectionInfo connection,
        IReadOnlyCollection<TableId> tables,
        Action<string> logMessage,
        CancellationToken ct);

    Task<bool> CreateDatabaseAsync(
        ConnectionInfo connection,
        string newDbName,
        Action<string> logMessage,
        CancellationToken ct);

    Task<bool> HasDataAsync(ConnectionInfo connection, CancellationToken ct);

    /// <summary>
    /// Lists all non-template databases available on the server.
    /// Connects to the 'postgres' maintenance database using the provided credentials.
    /// </summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionInfo connection, CancellationToken ct);

    Task<string?> TestConnectionAsync(ConnectionInfo connection, CancellationToken ct);
}
