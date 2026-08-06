namespace DbClone.Application.DTOs;

/// <summary>
/// Detected capabilities of a PostgreSQL server.
/// </summary>
public sealed record ServerCapabilities(
    string ServerVersion,
    bool IsSuperuser,
    bool SupportsBinaryCopy,
    bool SupportsPartitioning,
    bool SupportsIdentityColumns,
    bool SupportsGeneratedColumns,
    bool SupportsLogicalReplication,
    bool SupportsRowLevelSecurity,
    bool SupportsSessionReplicationRole,
    IReadOnlyList<string> InstalledExtensions);
