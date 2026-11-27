using DbClone.Application.Catalog;
using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbClone.Application.Interchange;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Service that executes the restore (catalog → database) workflow.
/// It prepares a populated target only after the user explicitly chooses update or create-new.
/// </summary>
public sealed class RestoreWorkflowService : IWorkflowService<RestoreRequest, RestoreResult>
{
    private readonly ITableCatalogService _catalogService;
    private readonly IConfirmationService _confirmationService;
    private readonly IInterchangeService _interchangeService;
    private readonly ILogger<RestoreWorkflowService> _logger;
    private readonly IServerAdminService _serverAdmin;

    public RestoreWorkflowService(
        IInterchangeService interchangeService,
        ITableCatalogService catalogService,
        IConfirmationService confirmationService,
        IServerAdminService serverAdmin,
        ILogger<RestoreWorkflowService> logger)
    {
        _interchangeService = interchangeService ?? throw new ArgumentNullException(nameof(interchangeService));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        _serverAdmin = serverAdmin ?? throw new ArgumentNullException(nameof(serverAdmin));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RestoreResult> ExecuteAsync(RestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogInformation("Restore workflow executing: catalog={CatalogPath}", request.CatalogPath);

        var prepared = await PrepareTargetAsync(request, ct);
        if (prepared.EarlyResult is not null)
            return prepared.EarlyResult;

        var interchangeRequest = new InterchangeRestoreRequest(
            CatalogPath: request.CatalogPath,
            Connection: prepared.Connection!,
            UnsupportedFeaturePolicy: request.UnsupportedFeaturePolicy);

        var result = await _interchangeService.RestoreAsync(interchangeRequest, request.Progress, ct);
        return MapResult(result);
    }

    private async Task<(ConnectionInfo? Connection, RestoreResult? EarlyResult)> PrepareTargetAsync(
        RestoreRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Connection.DatabaseName))
        {
            var createNew = await _confirmationService.ConfirmAsync(
                "Create new restore database?",
                "The destination connection does not name a database. Create a new database and restore the catalog there?");
            return createNew
                ? await PrepareNewTargetAsync(request.Connection, ct)
                : (null, Cancelled());
        }

        var serverVersion = await _serverAdmin.TestConnectionAsync(request.Connection, ct);
        if (serverVersion is null)
            return (null, Failed("Cannot connect to the selected restore database."));

        var refusal = await _interchangeService.DescribeRestoreRefusalAsync(request.Connection, ct);
        if (refusal is null)
            return (request.Connection, null);

        var choice = await _confirmationService.ConfirmRestoreTargetAsync(
            "Restore target is not empty",
            $"{refusal} Choose Update selected database to remove them, or Create new database "
            + $"to preserve '{request.Connection.DatabaseName}'.");

        return choice switch
        {
            ERestoreTargetChoice.UpdateSelected => await PrepareUpdatedTargetAsync(request.Connection, ct),
            ERestoreTargetChoice.CreateNew => await PrepareNewTargetAsync(request.Connection, ct),
            _ => (null, Cancelled()),
        };
    }

    private async Task<(ConnectionInfo? Connection, RestoreResult? EarlyResult)> PrepareUpdatedTargetAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var cleaned = await _catalogService.CleanTargetDatabaseAsync(
            connection,
            message => _logger.LogInformation("Restore cleanup: {Message}", message),
            ct);
        if (!cleaned)
            return (null, Failed("The selected database could not be cleaned. No restore was started."));

        // The clean reports what it could not drop as a warning and still finishes, so the only
        // evidence that the target is now one a restore accepts is the restore's own check, asked
        // again. Without this the run reached the product's refusal and the user, who had just asked
        // for the database to be overwritten, was told only that it was not empty.
        var refusal = await _interchangeService.DescribeRestoreRefusalAsync(connection, ct);
        return refusal is null
            ? (connection, null)
            : (null, Failed(
                $"The selected database is still not empty after cleaning: {refusal} No restore was started."));
    }

    private async Task<(ConnectionInfo? Connection, RestoreResult? EarlyResult)> PrepareNewTargetAsync(
        ConnectionInfo connection,
        CancellationToken ct)
    {
        var databases = await _serverAdmin.ListDatabasesAsync(connection, ct);
        var baseName = "restore_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var candidate = baseName;
        for (var suffix = 1; databases.Contains(candidate, StringComparer.OrdinalIgnoreCase); suffix++)
            candidate = $"{baseName}_{suffix}";

        var created = await _catalogService.CreateRestoreDatabaseAsync(
            connection,
            candidate,
            message => _logger.LogInformation("Restore database: {Message}", message),
            ct);
        return created
            ? (connection with { DatabaseName = candidate }, null)
            : (null, Failed($"Could not create restore database '{candidate}'. No restore was started."));
    }

    private static RestoreResult Cancelled() => new(
        Success: false,
        Message: "Restore cancelled; no database was changed.",
        ObjectsApplied: 0,
        RowsRestored: 0,
        ElapsedSeconds: 0,
        HasIssues: false,
        WasCancelled: true,
        WasRefused: false,
        RowCountMismatches: 0,
        TablesChecked: 0,
        LastCompletedChunk: null,
        Issues: []);

    private static RestoreResult Failed(string message) => new(
        Success: false,
        Message: message,
        ObjectsApplied: 0,
        RowsRestored: 0,
        ElapsedSeconds: 0,
        HasIssues: false,
        WasCancelled: false,
        WasRefused: false,
        RowCountMismatches: 0,
        TablesChecked: 0,
        LastCompletedChunk: null,
        Issues: []);

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
            TablesChecked: result.TablesChecked,
            LastCompletedChunk: result.LastCompletedChunk,
            Issues: result.Issues);
    }

    private static string BuildMessage(InterchangeRestoreResult result) => result.Outcome switch
    {
        ERestoreOutcome.Success => $"Restore succeeded: {result.ObjectsApplied} objects, {result.RowsRestored:N0} rows",
        ERestoreOutcome.CompletedWithIssues => SummarizeIssues(result),
        ERestoreOutcome.Cancelled => $"Restore cancelled after {result.ObjectsApplied} objects",
        ERestoreOutcome.Refused => IssueSummary.ForOutcome("Restore refused", result.Issues),
        ERestoreOutcome.Failed => IssueSummary.ForOutcome("Restore failed", result.Issues),
        _ => "Restore outcome unavailable"
    };

    /// <summary>
    /// States what a restore that ran did not carry. The object count is not the answer: a table whose
    /// rows the target refused is still an object the run created, so a restore that lost its data reads
    /// exactly like a complete one. The check after the data is what saw the loss, so its two numbers are
    /// the sentence — how many tables disagree with the catalog, out of how many it compared.
    /// </summary>
    private static string SummarizeIssues(InterchangeRestoreResult result) =>
        result.RowCountMismatches > 0 && result.TablesChecked > 0
            ? $"Restore completed with issues — {result.RowCountMismatches} of {result.TablesChecked} table(s) hold a different row count"
            : IssueSummary.ForOutcome("Restore completed with issues", result.Issues);
}
