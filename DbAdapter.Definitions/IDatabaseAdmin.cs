namespace DbAdapter.Definitions;

/// <summary>
/// The administration role: what may be done to the server the adapter was created for.
/// Contains no destructive operation — those live on <see cref="IDatabaseCleaner"/>.
/// </summary>
public interface IDatabaseAdmin
{
    /// <summary>Engine name shown to the user, e.g. "PostgreSQL".</summary>
    string ProviderName { get; }

    /// <summary>Server version of the target database, or null when the connection fails.</summary>
    Task<string?> TestConnectionAsync(CancellationToken ct);

    /// <summary>
    /// Lists all non-template databases available on the server.
    /// Connects to the maintenance database using the provided credentials.
    /// </summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct);

    /// <summary>Whether the target database holds any user data.</summary>
    Task<bool> HasDataAsync(CancellationToken ct);

    /// <summary>Creates <paramref name="newDbName"/> on the server. Logs progress through <paramref name="logMessage"/>.</summary>
    Task<bool> CreateDatabaseAsync(
        string newDbName,
        Action<string> logMessage,
        CancellationToken ct);

    /// <summary>
    /// Sanitizes a raw prefix into a valid database name for backup naming.
    /// The result is safe for use as a PostgreSQL identifier.
    /// </summary>
    string BuildBackupDatabaseName(string rawPrefix);
}
