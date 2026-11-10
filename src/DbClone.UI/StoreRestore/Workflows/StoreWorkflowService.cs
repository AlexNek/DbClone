using System.IO;
using DbClone.Application.Interchange;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Service that executes the store (database → catalog) workflow.
/// Single responsibility: orchestrate store execution via <see cref="IInterchangeService"/>.
/// </summary>
public sealed class StoreWorkflowService : IWorkflowService<StoreRequest, StoreResult>
{
    private readonly IInterchangeService _interchangeService;
    private readonly ILogger<StoreWorkflowService> _logger;

    public StoreWorkflowService(IInterchangeService interchangeService, ILogger<StoreWorkflowService> logger)
    {
        _interchangeService = interchangeService ?? throw new ArgumentNullException(nameof(interchangeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StoreResult> ExecuteAsync(StoreRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Store workflow executing: catalog={CatalogPath}", request.CatalogPath);

        var interchangeRequest = new InterchangeStoreRequest(
            Connection: request.Connection,
            TableSelection: request.TableSelection ?? Application.TableFilter.TableSelectionSpec.All,
            CatalogPath: request.CatalogPath,
            ChunkBytes: request.ChunkBytes,
            RowsPerChunk: request.RowsPerChunk,
            IncludeData: request.IncludeData,
            SnapshotConsistency: request.SnapshotConsistency,
            UnsupportedFeaturePolicy: request.UnsupportedFeaturePolicy,
            VerifyAfterStore: true,
            IncludeSummaryReport: request.IncludeSummaryReport);

        var result = await _interchangeService.StoreAsync(interchangeRequest, progress: null, ct);
        return MapResult(result, request.CatalogPath, request.IncludeSummaryReport);
    }

    private static StoreResult MapResult(InterchangeStoreResult result, string catalogPath, bool summaryRequested)
    {
        var success = result.Outcome is EStoreOutcome.Success or EStoreOutcome.CompletedWithIssues;

        return new StoreResult(
            Success: success,
            Message: BuildMessage(result),
            CatalogPath: result.CatalogPath ?? catalogPath,
            TablesExported: result.TablesExported,
            RowsExported: result.RowsExported,
            ElapsedSeconds: result.Elapsed.TotalSeconds,
            HasIssues: result.Outcome == EStoreOutcome.CompletedWithIssues,
            VerificationPassed: result.VerificationPassed,
            SummaryPath: result.SummaryPath,
            SummaryRequested: summaryRequested);
    }

    private static string BuildMessage(InterchangeStoreResult result)
    {
        var summary = string.IsNullOrEmpty(result.SummaryPath) ? string.Empty : ", summary written";
        return result.Outcome switch
        {
            EStoreOutcome.Success => $"Store succeeded: {result.TablesExported} tables, {result.RowsExported:N0} rows{summary}",
            EStoreOutcome.CompletedWithIssues => $"Store completed with issues: {result.TablesExported} tables{summary}",
            EStoreOutcome.Incomplete => "Store incomplete — catalog not usable",
            EStoreOutcome.Failed => "Store failed",
            _ => "Unknown outcome"
        };
    }
}
