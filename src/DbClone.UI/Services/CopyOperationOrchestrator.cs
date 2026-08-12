using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.Services;

/// <summary>
/// Orchestrates the database copy workflow, isolated from UI concerns.
/// Responsible for:
/// - Verifying source/destination connections
/// - Checking permissions
/// - Creating backup database (if needed)
/// - Executing the copy pipeline
/// - Reporting progress and errors via <see cref="ICopyProgressListener"/>
/// </summary>
public sealed class CopyOperationOrchestrator
{
    private readonly ICopyEngine _copyEngine;

    private readonly IDatabaseService _dbService;

    private readonly IDialogService _dialogService;

    private readonly ILogger<CopyOperationOrchestrator> _logger;

    private readonly IDatabaseMaintenanceProvider _maintenanceProvider;

    public CopyOperationOrchestrator(
        IDatabaseService dbService,
        ICopyEngine copyEngine,
        IDialogService dialogService,
        IDatabaseMaintenanceProvider maintenanceProvider,
        ILogger<CopyOperationOrchestrator> logger)
    {
        _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
        _copyEngine = copyEngine ?? throw new ArgumentNullException(nameof(copyEngine));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _maintenanceProvider = maintenanceProvider
                               ?? throw new ArgumentNullException(nameof(maintenanceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes the complete copy workflow: verify connections, check permissions, create backup if needed, run pipeline.
    /// </summary>
    public async Task<CopyWorkflowResult> ExecuteAsync(
        ConnectionViewModel source,
        ConnectionViewModel destination,
        ECopyMode copyMode,
        CopyRequest copyRequest,
        ICopyProgressListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Copy operation started");
            listener.OnPhaseChanged(ECopyOperationPhase.Initializing);

            // Step 1: Verify source connection
            listener.OnPhaseChanged(ECopyOperationPhase.CheckingSourceConnection);
            listener.OnStatusMessageChanged("Checking source connection...");
            listener.OnLogMessage(
                $"Checking source: {source.Host}:{source.PortNumber}/{source.DatabaseName}");

            var sourceVersion = await _dbService.TestConnectionAsync(source, cancellationToken);
            if (sourceVersion == null)
            {
                listener.OnPhaseChanged(ECopyOperationPhase.Failed);
                listener.OnStatusMessageChanged("Cannot connect to source");
                listener.OnLogMessage("Source connection failed");
                return new CopyWorkflowResult
                           {
                               Success = false, ErrorMessage = "Cannot connect to source"
                           };
            }

            listener.OnLogMessage(
                $"Source OK: {_maintenanceProvider.ProviderName} {sourceVersion}");

            // Log selected copy options
            var opts = copyRequest.Options;
            listener.OnLogHint(
                "pg_catalog, information_schema, pg_toast: managed by PostgreSQL — presence checked, contents not copied.");
            if (opts.ExcludePlatformSchemas)
                listener.OnLogHint(
                    "Platform schemas (owned by non-login service roles) are excluded.");
            else
                listener.OnLogHint(
                    "Platform schemas are included. Uncheck 'Platform Schemas' in Copy Options to exclude them.");
            listener.OnLogMessage(
                $"Options: Mode={opts.CopyMode}, Data={opts.CopyData}, Indexes={opts.CopyIndexes}, Views={opts.CopyViews}, Functions={opts.CopyFunctions}, Triggers={opts.CopyTriggers}, PlatformSchemas={(opts.ExcludePlatformSchemas ? "Excluded" : "Included")}");
            if (!opts.CopyIndexes)
                listener.OnLogHint(
                    "Indexes off: only secondary indexes are skipped — primary key indexes are always created with table structures.");

            // Read metadata to update object counts
            var metadata = await _dbService.ReadDatabaseMetadataAsync(source, cancellationToken);
            listener.OnLogMessage($"Source: {metadata.Summary}");

            // Step 2: Verify destination connection
            listener.OnPhaseChanged(ECopyOperationPhase.CheckingDestinationConnection);
            listener.OnStatusMessageChanged("Checking destination...");
            listener.OnLogMessage(
                $"Checking destination: {destination.Host}:{destination.PortNumber}/{destination.DatabaseName}");

            var destVersion = await _dbService.TestConnectionAsync(destination, cancellationToken);
            if (destVersion == null)
            {
                listener.OnPhaseChanged(ECopyOperationPhase.Failed);
                listener.OnStatusMessageChanged("Cannot connect to destination");
                listener.OnLogMessage("Destination connection failed");
                return new CopyWorkflowResult
                           {
                               Success = false, ErrorMessage = "Cannot connect to destination"
                           };
            }

            listener.OnLogMessage(
                $"Destination OK: {_maintenanceProvider.ProviderName} {destVersion}");

            // Step 3: Check destination permissions
            listener.OnPhaseChanged(ECopyOperationPhase.CheckingPermissions);
            listener.OnStatusMessageChanged("Verifying destination permissions...");
            var requiredPermissions = EPermissionCheck.Connect | EPermissionCheck.CreateObjects
                                                               | EPermissionCheck.InsertData;
            if (copyMode == ECopyMode.Backup)
                requiredPermissions |= EPermissionCheck.CreateDatabase;
            if (copyMode != ECopyMode.Backup)
                requiredPermissions |= EPermissionCheck.DropObjects;

            var permIssues = await _dbService.CheckPermissionsAsync(
                                 destination,
                                 requiredPermissions,
                                 cancellationToken);
            if (permIssues.Count > 0)
            {
                foreach (var issue in permIssues)
                    listener.OnLogMessage($"Permission issue: {issue}");

                listener.OnPhaseChanged(ECopyOperationPhase.Failed);
                listener.OnStatusMessageChanged("Insufficient permissions on destination");
                listener.OnLogMessage("Permission check failed");
                return new CopyWorkflowResult { Success = false, ErrorMessage = permIssues[0] };
            }

            listener.OnLogMessage("Permission check passed");

            // Step 4: Handle backup mode (create new database)
            var destinationToUse = destination;
            if (copyMode == ECopyMode.Backup)
            {
                var backupResult = await CreateBackupDatabaseAsync(
                                       source,
                                       destination,
                                       listener,
                                       cancellationToken);
                if (!backupResult.Success)
                {
                    listener.OnPhaseChanged(ECopyOperationPhase.Failed);
                    listener.OnStatusMessageChanged("Failed to create backup database");
                    return backupResult;
                }

                destinationToUse = backupResult.BackupDestination!;
                listener.OnLogMessage(
                    $"Backup database created: {backupResult.BackupDatabaseName}");

                // Point the pipeline at the newly created backup database.
                // copyRequest.Destination is an immutable snapshot taken before
                // the backup DB existed — without this the pipeline connects to
                // the original destination (which already has data).
                copyRequest = copyRequest with
                {
                    Destination = ConnectionInfoFactory.FromViewModel(destinationToUse)
                };
            }

            // Step 5: Handle destination cleanup (if not backup mode)
            if (copyMode != ECopyMode.Backup)
            {
                listener.OnPhaseChanged(ECopyOperationPhase.CheckingDestination);
                var destHasData = await _dbService.CheckDestinationHasDataAsync(
                                      destinationToUse,
                                      cancellationToken);
                if (destHasData)
                {
                    var selection = opts.TableSelection;
                    var selectionActive = selection is { IsActive: true };

                    // Two valid scenarios with an active table selection — the
                    // user chooses explicitly right here:
                    // 1. Refresh only the selected tables — unselected destination
                    //    tables stay untouched (selection-scoped clean).
                    // 2. Clear the entire destination — afterwards it contains
                    //    ONLY the selected tables.
                    var clearAll = !selectionActive;

                    if (selectionActive)
                    {
                        var choiceMessage =
                            $"The destination database '{destinationToUse.DatabaseName}' on {destinationToUse.Host} already contains data, "
                            + "and a TABLE SELECTION is active.\n\n"
                            + "How should the destination be cleaned?";

                        var choice = await _dialogService.ConfirmSelectionCleanAsync(
                                         "Clean Target Database?",
                                         choiceMessage);

                        if (choice == ESelectionCleanChoice.Cancel)
                        {
                            listener.OnPhaseChanged(ECopyOperationPhase.Cancelled);
                            listener.OnLogMessage("Operation cancelled by user");
                            return new CopyWorkflowResult
                                       {
                                           Success = false, ErrorMessage = "Operation cancelled by user"
                                       };
                        }

                        clearAll = choice == ESelectionCleanChoice.ClearEntireDestination;
                        listener.OnLogMessage(
                            clearAll
                                ? "Clearing the ENTIRE destination — after the copy it will contain only the selected tables"
                                : "Replacing only the selected tables — all other destination tables remain untouched");
                    }
                    else
                    {
                        var confirmMessage =
                            $"The destination database '{destinationToUse.DatabaseName}' on {destinationToUse.Host} already contains data.\n\n"
                            + "All existing tables, views, functions, and other objects will be DROPPED.\n\n"
                            + "Click YES to clean and overwrite, or NO to cancel.";

                        var confirm = await _dialogService.ConfirmAsync(
                                          "Clean Target Database?",
                                          confirmMessage);

                        if (!confirm)
                        {
                            listener.OnPhaseChanged(ECopyOperationPhase.Cancelled);
                            listener.OnLogMessage("Operation cancelled by user");
                            return new CopyWorkflowResult
                                       {
                                           Success = false, ErrorMessage = "Operation cancelled by user"
                                       };
                        }
                    }

                    listener.OnPhaseChanged(ECopyOperationPhase.CleaningDestination);
                    var cleaned = clearAll
                        ? await _dbService.CleanTargetDatabaseAsync(
                              destinationToUse,
                              msg => listener.OnLogMessage(msg),
                              cancellationToken)
                        : await _dbService.CleanTargetSelectionAsync(
                              source,
                              destinationToUse,
                              selection!,
                              msg => listener.OnLogMessage(msg),
                              cancellationToken);

                    if (!cleaned)
                    {
                        listener.OnPhaseChanged(ECopyOperationPhase.Failed);
                        listener.OnStatusMessageChanged("Failed to clean destination database");
                        return new CopyWorkflowResult
                                   {
                                       Success = false,
                                       ErrorMessage = clearAll
                                           ? "Failed to clean destination database — see log for details"
                                           : "Selection-scoped cleanup failed or aborted — see log for details"
                                   };
                    }

                    listener.OnLogMessage("Destination database cleaned");
                }
                else
                {
                    listener.OnLogMessage("Destination is empty, no drop needed");
                }
            }

            // Step 6: Execute copy pipeline
            listener.OnPhaseChanged(ECopyOperationPhase.RunningPipeline);
            listener.OnStatusMessageChanged("Running pipeline...");

            var progressHandler = new Progress<CopyProgress>(p =>
                {
                    listener.OnProgressChanged(p);
                    if (p.CompletedStage is { } stage)
                        listener.OnStageCompleted(stage);
                });

            var result = await _copyEngine.ExecuteCopyAsync(
                             copyRequest,
                             progressHandler,
                             cancellationToken);

            listener.OnPhaseChanged(
                result.Success ? ECopyOperationPhase.Completed : ECopyOperationPhase.Failed);
            listener.OnStatusMessageChanged(
                result.Success
                    ? $"Copy complete in {result.TotalDuration.TotalSeconds:F1}s"
                    : $"Copy failed: {result.Errors.Count} errors");

            return new CopyWorkflowResult
                       {
                           Success = result.Success,
                           ErrorMessage = result.Success
                               ? null
                               : result.Errors.Count == 1
                                   ? StageDetailRenderer.RenderError(result.Errors.First())
                                   : $"{result.Errors.Count} errors — see UI log for details",
                           Result = result
                       };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Copy operation cancelled");
            listener.OnPhaseChanged(ECopyOperationPhase.Cancelled);
            listener.OnStatusMessageChanged("Operation cancelled");
            listener.OnLogMessage("Operation cancelled by user");
            return new CopyWorkflowResult { Success = false, ErrorMessage = "Operation cancelled" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy operation failed with exception");
            listener.OnPhaseChanged(ECopyOperationPhase.Failed);
            listener.OnStatusMessageChanged($"Error: {ex.Message}");
            listener.OnLogMessage($"Unexpected error: {ex.Message}");
            listener.OnError(
                new CopyError(
                    ECopyStage.Orchestration,
                    EStageMessageKind.Exception,
                    null,
                    new Dictionary<string, object> { [PropKeys.Reason] = ex.Message },
                    ex));
            return new CopyWorkflowResult
                       {
                           Success = false, ErrorMessage = ex.Message, Exception = ex
                       };
        }
        finally
        {
            listener.OnOperationComplete();
        }
    }

    private async Task<CopyWorkflowResult> CreateBackupDatabaseAsync(
        ConnectionViewModel source,
        ConnectionViewModel destination,
        ICopyProgressListener listener,
        CancellationToken cancellationToken)
    {
        listener.OnPhaseChanged(ECopyOperationPhase.CreatingBackupDatabase);
        listener.OnStatusMessageChanged("Creating backup database...");

        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var rawPrefix = !string.IsNullOrWhiteSpace(source.SelectedSavedConnection?.BackupName)
                                ? source.SelectedSavedConnection.BackupName
                                : !string.IsNullOrWhiteSpace(source.DatabaseName)
                                    ? source.DatabaseName
                                    : !string.IsNullOrWhiteSpace(source.Username)
                                        ? source.Username
                                        : "backup";

            var resolvedSourceDb = SanitizePgIdentifier(rawPrefix);
            var backupDbName = $"{resolvedSourceDb}_backup_{timestamp}";

            listener.OnLogMessage($"Backup mode: creating new database = {backupDbName}");

            var created = await _dbService.CreateBackupDatabaseAsync(
                              destination,
                              backupDbName,
                              msg => listener.OnLogMessage(msg),
                              cancellationToken);

            if (!created)
            {
                listener.OnLogMessage("Failed to create backup database");
                return new CopyWorkflowResult
                           {
                               Success = false, ErrorMessage = "Failed to create backup database"
                           };
            }

            // Update destination database name to point to the backup database
            destination.DatabaseName = backupDbName;

            return new CopyWorkflowResult
                       {
                           Success = true,
                           BackupDatabaseName = backupDbName,
                           BackupDestination = destination
                       };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup database");
            listener.OnLogMessage($"Error creating backup database: {ex.Message}");
            return new CopyWorkflowResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static string SanitizePgIdentifier(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "db";

        var sb = new System.Text.StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(char.ToLowerInvariant(c));
        }

        var result = sb.ToString();
        if (string.IsNullOrEmpty(result))
            return "db";

        // PostgreSQL identifiers cannot start with a digit
        if (char.IsDigit(result[0]))
            result = "db_" + result;

        return result.Length > 63 ? result[..63] : result;
    }
}
