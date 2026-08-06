using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Copy;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Pipeline;

/// <summary>
/// PostgreSQL implementation of <see cref="ICopyEngine"/>.
/// Orchestrates the full database copy operation.
/// </summary>
public sealed class PgCopyEngine : ICopyEngine
{
    private readonly ILogger<PgCopyEngine> _logger;

    private readonly ICopyPipeline _pipeline;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgCopyEngine"/> class.
    /// </summary>
    public PgCopyEngine(ICopyPipeline pipeline, ILogger<PgCopyEngine> logger)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CopyResult> ExecuteCopyAsync(
        CopyRequest request,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting copy: {Source} -> {Destination}",
            $"{request.Source.Host}:{request.Source.Port}/{request.Source.DatabaseName}",
            $"{request.Destination.Host}:{request.Destination.Port}/{request.Destination.DatabaseName}");

        var context = new CopyContext { Request = request, Progress = progress };

        try
        {
            var result = await _pipeline.ExecuteAsync(context, cancellationToken);

            progress?.Report(
                new CopyProgress(
                    CurrentStage: result.Success ? ECopyStage.Complete : ECopyStage.Failed,
                    CompletedStages: result.StageResults.Count,
                    TotalStages: result.StageResults.Count,
                    PercentComplete: result.Success ? 100 : 0,
                    ElapsedSeconds: result.TotalDuration.TotalSeconds));

            _logger.LogInformation(
                "Copy {Status} in {Duration}",
                result.Success ? "succeeded" : "failed",
                result.TotalDuration);

            return result;
        }
        finally
        {
            // Clean up connections
            if (context.SourceConnection is NpgsqlConnection sourceConn)
            {
                await sourceConn.CloseAsync();
                await sourceConn.DisposeAsync();
            }

            if (context.DestinationConnection is NpgsqlConnection destConn)
            {
                await destConn.CloseAsync();
                await destConn.DisposeAsync();
            }
        }
    }
}
