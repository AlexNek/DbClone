using System.Diagnostics;

using DbClone.Application.DTOs;
using DbClone.Application.Enums;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Copy;

/// <summary>
/// Orchestrates execution of copy pipeline stages in order.
/// Provider-agnostic — stages are injected by the concrete provider (PostgreSql, MySql, etc.).
/// </summary>
public sealed class CopyPipeline : ICopyPipeline
{
    private readonly ILogger<CopyPipeline> _logger;

    private readonly IEnumerable<ICopyStage> _stages;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopyPipeline"/> class.
    /// </summary>
    public CopyPipeline(IEnumerable<ICopyStage> stages, ILogger<CopyPipeline> logger)
    {
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CopyResult> ExecuteAsync(
        CopyContext context,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        var orderedStages = _stages.OrderBy(s => s.Order).ToList();
        var totalStages = orderedStages.Count;

        context.TotalStages = totalStages;
        context.TotalStopwatch = totalSw;

        _logger.LogInformation("Executing pipeline with {StageCount} stages", totalStages);

        for (var i = 0; i < orderedStages.Count; i++)
        {
            var stage = orderedStages[i];
            cancellationToken.ThrowIfCancellationRequested();

            // Heartbeat: ping both connections between stages to prevent proxy/firewall idle drops.
            if (context.ConnectionHeartbeat is not null)
            {
                try
                {
                    await context.ConnectionHeartbeat(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Connection heartbeat failed between stages");
                }
            }

            _logger.LogInformation(
                "Stage {Index}/{Total}: {StageName}",
                i + 1,
                totalStages,
                stage.Name);

            // Report stage start so the UI can show the in-progress state
            // (CompletedStage = null distinguishes start from completion).
            context.Progress?.Report(
                new CopyProgress(
                    stage.Name,
                    i,
                    totalStages,
                    (double)i / totalStages * 100,
                    totalSw.Elapsed.TotalSeconds));

            var stageSw = Stopwatch.StartNew();

            try
            {
                var errorsBefore = context.Errors.Count;
                var result = await stage.ExecuteAsync(context, cancellationToken);
                stageSw.Stop();

                var stageResult = new StageResult(
                    StageName: stage.Name,
                    Success: result.Success,
                    Duration: stageSw.Elapsed,
                    ObjectsProcessed: result.ObjectsProcessed,
                    Details: result.Details);

                context.StageResults.Add(stageResult);

                context.Progress?.Report(
                    new CopyProgress(
                        stage.Name,
                        i + 1,
                        totalStages,
                        (double)(i + 1) / totalStages * 100,
                        totalSw.Elapsed.TotalSeconds,
                        stageResult));

                if (!result.Success)
                {
                    // Only add a pipeline-level error if the stage did not already
                    // push descriptive errors into context.Errors during execution.
                    // This prevents a generic "Stage failed" from masking the real reason.
                    if (context.Errors.Count == errorsBefore)
                    {
                        var firstFailure = result.Details.FirstOrDefault(
                            d => d.Level == ELogLevel.Error && d.Kind is EStageMessageKind.Failed
                                or EStageMessageKind.Exception or EStageMessageKind.StillMismatched
                                or EStageMessageKind.Skipped);

                        context.Errors.Add(
                            new CopyError(
                                StageName: stage.Name,
                                Kind: firstFailure?.Kind ?? EStageMessageKind.Failed,
                                ObjectName: firstFailure?.ObjectName,
                                Properties: firstFailure?.Properties,
                                Exception: null));
                    }

                    _logger.LogError(
                        "Stage {StageName} failed after {Duration}",
                        stage.Name,
                        stageSw.Elapsed);

                    if (IsCriticalStage(stage))
                    {
                        _logger.LogError(
                            "Critical stage {StageName} failed, aborting pipeline",
                            stage.Name);
                        break;
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "Stage {StageName} completed: {Objects} objects in {Duration}",
                        stage.Name,
                        result.ObjectsProcessed,
                        stageSw.Elapsed);
                }
            }
            catch (OperationCanceledException)
            {
                stageSw.Stop();
                _logger.LogWarning("Pipeline cancelled during stage {StageName}", stage.Name);

                context.StageResults.Add(
                    new StageResult(
                        StageName: stage.Name,
                        Success: false,
                        Duration: stageSw.Elapsed,
                        ObjectsProcessed: 0,
                        Details: [StageDetail.Cancelled()]));

                throw;
            }
            catch (Exception ex)
            {
                stageSw.Stop();
                _logger.LogError(
                    ex,
                    "Stage {StageName} threw exception after {Duration}",
                    stage.Name,
                    stageSw.Elapsed);

                var stageResult = new StageResult(
                    StageName: stage.Name,
                    Success: false,
                    Duration: stageSw.Elapsed,
                    ObjectsProcessed: 0,
                    Details: [StageDetail.Exception(ex.Message)]);

                context.StageResults.Add(stageResult);

                context.Progress?.Report(
                    new CopyProgress(
                        stage.Name,
                        i + 1,
                        totalStages,
                        (double)(i + 1) / totalStages * 100,
                        totalSw.Elapsed.TotalSeconds,
                        stageResult));

                context.Errors.Add(
                    new CopyError(
                        StageName: stage.Name,
                        Kind: EStageMessageKind.Exception,
                        ObjectName: null,
                        Properties: new Dictionary<string, object>
                        {
                            [PropKeys.Reason] = ex.Message
                        },
                        Exception: ex));

                if (IsCriticalStage(stage))
                {
                    _logger.LogError(
                        "Critical stage {StageName} failed, aborting pipeline",
                        stage.Name);
                    break;
                }
            }
        }

        totalSw.Stop();

        var success = context.Errors.Count == 0;

        _logger.LogInformation(
            "Pipeline completed: {Status} in {Duration} ({StageCount} stages, {ErrorCount} errors)",
            success ? "SUCCESS" : "FAILED",
            totalSw.Elapsed,
            context.StageResults.Count,
            context.Errors.Count);

        return new CopyResult(
            Success: success,
            TotalDuration: totalSw.Elapsed,
            StageResults: context.StageResults,
            Warnings: context.Warnings,
            Errors: context.Errors,
            Statistics: context.Statistics);
    }

    private static bool IsCriticalStage(ICopyStage stage) =>
        stage.Order <= 30 || stage.Name == ECopyStage.CopyData;
}
