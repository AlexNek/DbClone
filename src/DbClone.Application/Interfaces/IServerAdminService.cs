using DbClone.Application.DTOs;

namespace DbClone.Application.Interfaces;

/// <summary>
/// The server facts the connection form asks for, reached through the adapter's admin
/// role so no UI file names a definitions type (D5, D9). The destructive operations stay
/// on the cleaner and are never exposed here.
/// </summary>
public interface IServerAdminService
{
    /// <summary>Engine name shown to the user, e.g. "PostgreSQL".</summary>
    string GetProviderName(ConnectionInfo connection);

    /// <summary>Server version of the target database, or null when the connection fails.</summary>
    Task<string?> TestConnectionAsync(ConnectionInfo connection, CancellationToken ct);

    /// <summary>Lists all non-template databases available on the server.</summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionInfo connection, CancellationToken ct);
}
