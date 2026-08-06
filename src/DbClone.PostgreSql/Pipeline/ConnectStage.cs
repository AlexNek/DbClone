using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.Application.Platforms;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Opens connections to source and destination databases.
/// </summary>
public sealed class ConnectStage : ICopyStage
{
    private readonly ILogger<ConnectStage> _logger;
    private readonly PlatformSchemaResolver _platformResolver;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.Connect;

    /// <inheritdoc />
    public int Order => 10;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectStage"/> class.
    /// </summary>
    public ConnectStage(ILogger<ConnectStage> logger, PlatformSchemaResolver platformResolver)
    {
        _logger = logger;
        _platformResolver = platformResolver;
    }

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        var sourceConnStr =
            PgConnectionStringBuilder.BuildCopyConnectionString(context.Request.Source);
        var destConnStr =
            PgConnectionStringBuilder.BuildCopyConnectionString(context.Request.Destination);

        var sourceLabel =
            $"{context.Request.Source.Host}:{context.Request.Source.Port}/{context.Request.Source.DatabaseName}";
        var destLabel =
            $"{context.Request.Destination.Host}:{context.Request.Destination.Port}/{context.Request.Destination.DatabaseName}";

        var details = new List<StageDetail>();

        // Connect to source
        _logger.LogInformation("Connecting to source: {Server}", sourceLabel);

        NpgsqlConnection sourceConn;
        try
        {
            sourceConn = new NpgsqlConnection(sourceConnStr);
            await sourceConn.OpenAsync(cancellationToken);
            details.Add(
                StageDetail.VersionInfo(ECompareSide.Source, sourceConn.ServerVersion));
            _logger.LogInformation(
                "Source connected: {Server}, version {Version}",
                sourceLabel,
                sourceConn.ServerVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to source server {Server}", sourceLabel);
            details.Add(StageDetail.Exception(ex.Message));
            return new StageResult(
                Name,
                false,
                TimeSpan.Zero,
                0,
                details);
        }

        // Connect to destination
        _logger.LogInformation("Connecting to destination: {Server}", destLabel);

        NpgsqlConnection destConn;
        try
        {
            destConn = new NpgsqlConnection(destConnStr);
            await destConn.OpenAsync(cancellationToken);
            details.Add(
                StageDetail.VersionInfo(ECompareSide.Destination, destConn.ServerVersion));
            _logger.LogInformation(
                "Destination connected: {Server}, version {Version}",
                destLabel,
                destConn.ServerVersion);
        }
        catch (Exception ex)
        {
            await sourceConn.DisposeAsync();
            _logger.LogError(ex, "Failed to connect to destination server {Server}", destLabel);
            details.Add(StageDetail.Exception(ex.Message));
            return new StageResult(
                Name,
                false,
                TimeSpan.Zero,
                0,
                details);
        }

        context.SourceConnection = sourceConn;
        context.DestinationConnection = destConn;
        context.SourceConnectionString = sourceConnStr;
        context.DestinationConnectionString = destConnStr;

        // Resolve platform definitions for both connections
        context.SourcePlatformResolution = _platformResolver.Resolve(
            "postgresql", context.Request.Source.Host, sourceConn.ServerVersion);
        context.DestinationPlatformResolution = _platformResolver.Resolve(
            "postgresql", context.Request.Destination.Host, destConn.ServerVersion);

        // Surface version warnings (unsupported server version)
        if (context.SourcePlatformResolution.VersionWarning is { } srcWarning)
            details.Add(StageDetail.Infrastructure(srcWarning));
        if (context.DestinationPlatformResolution.VersionWarning is { } dstWarning)
            details.Add(StageDetail.Infrastructure(dstWarning));

        // Set up heartbeat to keep connections alive between stages.
        // Cloud proxies (e.g. Aiven) drop idle TCP connections after a few seconds.
        // Sending SELECT 1 between stages prevents the proxy from considering the connection idle.
        context.ConnectionHeartbeat = async (ctx, ct) =>
            {
                if (ctx.SourceConnection is NpgsqlConnection src
                    && src.State == System.Data.ConnectionState.Open)
                {
                    await using var cmd = new NpgsqlCommand("SELECT 1", src);
                    cmd.CommandTimeout = 10;
                    await cmd.ExecuteScalarAsync(ct);
                }

                if (ctx.DestinationConnection is NpgsqlConnection dst
                    && dst.State == System.Data.ConnectionState.Open)
                {
                    await using var cmd = new NpgsqlCommand("SELECT 1", dst);
                    cmd.CommandTimeout = 10;
                    await cmd.ExecuteScalarAsync(ct);
                }
            };

        return new StageResult(Name, true, TimeSpan.Zero, 2, details);
    }
}
