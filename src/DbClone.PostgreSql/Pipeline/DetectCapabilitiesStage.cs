using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Copy;
using DbClone.PostgreSql.Execution;
using DbClone.PostgreSql.Metadata;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// Detects capabilities of source and destination servers.
/// </summary>
public sealed class DetectCapabilitiesStage : ICopyStage
{
    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc />
    public ECopyStage Name => ECopyStage.DetectCapabilities;

    /// <inheritdoc />
    public int Order => 20;

    /// <summary>Initializes a new instance.</summary>
    public DetectCapabilitiesStage(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        var sourceConn = (NpgsqlConnection)context.SourceConnection!;
        var destConn = (NpgsqlConnection)context.DestinationConnection!;

        var sourceExec = new PgSqlExecutor(
            sourceConn,
            _loggerFactory.CreateLogger<PgSqlExecutor>());
        var destExec = new PgSqlExecutor(destConn, _loggerFactory.CreateLogger<PgSqlExecutor>());

        var sourceDetector = new PgCapabilityDetector(
            sourceExec,
            _loggerFactory.CreateLogger<PgCapabilityDetector>());
        var destDetector = new PgCapabilityDetector(
            destExec,
            _loggerFactory.CreateLogger<PgCapabilityDetector>());

        context.SourceCapabilities = await sourceDetector.DetectAsync(cancellationToken);
        context.DestinationCapabilities = await destDetector.DetectAsync(cancellationToken);

        return new StageResult(
            Name,
            true,
            TimeSpan.Zero,
            2,
                [
                    StageDetail.VersionInfo(ECompareSide.Source, context.SourceCapabilities.ServerVersion),
                    StageDetail.VersionInfo(ECompareSide.Destination, context.DestinationCapabilities.ServerVersion)
                ]);
    }
}
