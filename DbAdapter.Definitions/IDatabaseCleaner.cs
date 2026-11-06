using CanonicalModel.Enums;
using CanonicalModel.Models;

namespace DbAdapter.Definitions;

/// <summary>
/// The cleaning role: the destructive preparation of a target, and the permission facts
/// that decide whether it may be attempted.
/// </summary>
public interface IDatabaseCleaner
{
    /// <summary>
    /// Checks whether the user has sufficient permissions for the requested operations.
    /// Returns a list of missing permissions/problems. Empty list means all checks passed.
    /// </summary>
    Task<IReadOnlyList<string>> CheckPermissionsAsync(
        EPermissionCheck checks,
        CancellationToken ct);

    /// <summary>
    /// Drops all user objects from the target database.
    /// Returns false (without dropping anything) if the user lacks ownership
    /// of schemas present in the database — the caller must abort the copy.
    /// </summary>
    Task<bool> CleanDatabaseAsync(
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
        IReadOnlyCollection<TableId> tables,
        Action<string> logMessage,
        CancellationToken ct);
}
