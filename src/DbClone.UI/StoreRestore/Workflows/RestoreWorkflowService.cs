using DbClone.Application.Interchange;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Service that executes the restore (catalog → database) workflow.
/// Single responsibility: orchestrate restore execution via <see cref="IInterchangeService"/>.
/// </summary>
public sealed class RestoreWorkflowService : IWorkflowService<RestoreRequest, RestoreResult>
{
    private readonly IInterchangeService _interchangeService;
    private readonly ILogger<RestoreWorkflowService> _logger;

    public RestoreWorkflowService(IInterchangeService interchangeService, ILogger<RestoreWorkflowService> logger)
    {
        _interchangeService = interchangeService ?? throw new ArgumentNullException(nameof(interchangeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RestoreResult> ExecuteAsync(RestoreRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Restore workflow executing: catalog={CatalogPath}", request.CatalogPath);

        var interchangeRequest = new InterchangeRestoreRequest(
            CatalogPath: request.CatalogPath,
            Connection: request.Connection,
            UnsupportedFeaturePolicy: request.UnsupportedFeaturePolicy);

        var result = await _interchangeService.RestoreAsync(interchangeRequest, progress: null, ct);

        return MapResult(result);
    }

    private static RestoreResult MapResult(InterchangeRestoreResult result)
    {
        var success = result.Outcome is ERestoreOutcome.Success or ERestoreOutcome.CompletedWithIssues;

        return new RestoreResult(
            Success: success,
            Message: BuildMessage(result),
            ObjectsApplied: result.ObjectsApplied,
            RowsRestored: result.RowsRestored,
            ElapsedSeconds: result.Elapsed.TotalSeconds,
            HasIssues: result.Outcome == ERestoreOutcome.CompletedWithIssues,
            WasCancelled: result.Outcome == ERestoreOutcome.Cancelled,
            WasRefused: result.Outcome == ERestoreOutcome.Refused,
            RowCountMismatches: result.RowCountMismatches,
            LastCompletedChunk: result.LastCompletedChunk,
            Issues: result.Issues);
    }

    private static string BuildMessage(InterchangeRestoreResult result)
    {
        return result.Outcome switch
        {
            ERestoreOutcome.Success => $"Restore succeeded: {result.ObjectsApplied} objects, {result.RowsRestored:N0} rows",
            ERestoreOutcome.CompletedWithIssues => $"Restore completed with issues: {result.ObjectsApplied} objects",
            ERestoreOutcome.Cancelled => $"Restore cancelled after {result.ObjectsApplied} objects",
            ERestoreOutcome.Refused => "Restore refused — target database not empty",
            ERestoreOutcome.Failed => $"Restore failed: {result.Issues.Count} issue(s)",
            _ => "Restore outcome unavailable"
        };
    }
}
