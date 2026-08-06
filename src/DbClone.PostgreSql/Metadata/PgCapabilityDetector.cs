using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

namespace DbClone.PostgreSql.Metadata;

/// <summary>
/// PostgreSQL implementation of <see cref="ICapabilityDetector"/>.
/// Queries the connected server for version, features, and extensions.
/// </summary>
public sealed class PgCapabilityDetector : ICapabilityDetector
{
    private readonly PgSqlExecutor _executor;

    private readonly ILogger<PgCapabilityDetector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgCapabilityDetector"/> class.
    /// </summary>
    public PgCapabilityDetector(PgSqlExecutor executor, ILogger<PgCapabilityDetector> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ServerCapabilities> DetectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Detecting server capabilities");

        var serverVersion = await DetectServerVersionAsync(cancellationToken);
        var majorVersion = ParseMajorVersion(serverVersion);

        var isSuperuser = await DetectSuperuserAsync(cancellationToken);
        var extensions = await DetectExtensionsAsync(cancellationToken);

        var capabilities = new ServerCapabilities(
            ServerVersion: serverVersion,
            IsSuperuser: isSuperuser,
            SupportsBinaryCopy: true, // Binary COPY supported since PostgreSQL 7.4
            SupportsPartitioning: majorVersion >= 10,
            SupportsIdentityColumns: majorVersion >= 10,
            SupportsGeneratedColumns: majorVersion >= 12,
            SupportsLogicalReplication: majorVersion >= 10,
            SupportsRowLevelSecurity: majorVersion >= 10, // Actually 9.5, but round up
            SupportsSessionReplicationRole: isSuperuser,
            InstalledExtensions: extensions
        );

        _logger.LogInformation(
            "Server: {Version}, Superuser: {IsSuperuser}, Extensions: {ExtensionCount}",
            capabilities.ServerVersion,
            capabilities.IsSuperuser,
            capabilities.InstalledExtensions.Count);

        return capabilities;
    }

    private async Task<IReadOnlyList<string>> DetectExtensionsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _executor.QueryAsync(
                       "SELECT extname FROM pg_extension ORDER BY extname",
                       reader => reader.GetString(0),
                       cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect installed extensions");
            return [];
        }
    }

    private async Task<string> DetectServerVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await _executor.ExecuteScalarAsync<string>(
                              "SHOW server_version",
                              cancellationToken);
            return version;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect server version, using connection info");
            return "unknown";
        }
    }

    private async Task<bool> DetectSuperuserAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _executor.ExecuteScalarAsync<bool>(
                       "SELECT rolsuper FROM pg_roles WHERE rolname = current_user",
                       cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect superuser status");
            return false;
        }
    }

    private static int ParseMajorVersion(string versionString)
    {
        // Version strings like "16.3 (Ubuntu 16.3-1.pgdg22.04+1)" or "15.4"
        if (string.IsNullOrEmpty(versionString) || versionString == "unknown")
            return 0;

        var parts = versionString.Split(['.', ' '], 2);
        if (parts.Length > 0 && int.TryParse(parts[0], out var major))
            return major;

        return 0;
    }
}
