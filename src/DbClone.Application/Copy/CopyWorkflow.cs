using DbAdapter.Definitions;
using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using CanonicalModel.Models;
using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Copy;

/// <summary>
/// The full copy workflow: verify connections, check permissions, optionally create
/// a backup database, optionally clean the destination, resolve the table selection,
/// and run the copy session. Orchestration lives here (not in the UI) so that the
/// UI only supplies connections, options and a progress sink.
/// </summary>
public sealed class CopyWorkflow : ICopyWorkflow
{
    /// <summary>The log row a copy that runs no selection still reports.</summary>
    private static readonly StageResult NoSelectionStage = new(
        ECopyStage.ApplyTableFilter,
        true,
        TimeSpan.Zero,
        0,
        [StageDetail.Infrastructure("No table selection active", ELogLevel.Info)]);

    /// <summary>No engine stage ran, so nothing was copied.</summary>
    private static readonly CopyStatistics NothingCopied = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private readonly DatabaseTargetFactory _targetFactory;

    private readonly ITableFilterApplier _filterApplier;

    private readonly IConfirmationService _confirmationService;

    private readonly ILogger<CopyWorkflow> _logger;

    /// <summary>Initializes a new instance.</summary>
    public CopyWorkflow(
        DatabaseTargetFactory targetFactory,
        ITableFilterApplier filterApplier,
        IConfirmationService confirmationService,
        ILogger<CopyWorkflow> logger)
    {
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));
        _filterApplier = filterApplier ?? throw new ArgumentNullException(nameof(filterApplier));
        _confirmationService = confirmationService
                               ?? throw new ArgumentNullException(nameof(confirmationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CopyWorkflowResult> ExecuteAsync(
        ConnectionInfo source,
        ConnectionInfo destination,
        string? backupNamePrefix,
        CopyOptions options,
        TableSelectionSpec? tableSelection,
        ICopyProgressSink listener,
        CancellationToken cancellationToken)
    {
        ConnectionInfo? backupDestination = null;
        var adapter = _targetFactory.ResolveAdapter(source.Engine);
        try
        {
            _logger.LogInformation("Copy operation started");
            listener.OnPhaseChanged(ECopyOperationPhase.Initializing);

            var sourceTarget = _targetFactory.Create(source);
            var destTarget = _targetFactory.Create(destination);
            var sourceAdmin = adapter.CreateAdmin(sourceTarget);
            var destAdmin = adapter.CreateAdmin(destTarget);
            var destCleaner = adapter.CreateCleaner(destTarget);
            var sourceMetadata = adapter.CreateMetadataSource(sourceTarget);

            // Step 1: Verify source connection
            listener.OnPhaseChanged(ECopyOperationPhase.CheckingSourceConnection);
            listener.OnStatusMessageChanged("Checking source connection...");
            listener.OnLogMessage(
                $"Checking source: {source.Host}:{source.Port}/{source.DatabaseName}");

            var sourceVersion = await sourceAdmin.TestConnectionAsync(cancellationToken);
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
                $"Source OK: {sourceAdmin.ProviderName} {sourceVersion}");

            // Log selected copy options
            listener.OnLogHint(
                "System-managed objects: presence checked, contents not copied.");
            if (options.ExcludePlatformSchemas)
                listener.OnLogHint(
                    "Platform schemas (owned by non-login service roles) are excluded.");
            else
                listener.OnLogHint(
                    "Platform schemas are included. Uncheck 'Platform Schemas' in Copy Options to exclude them.");
            listener.OnLogMessage(
                $"Options: Mode={options.CopyMode}, Data={options.CopyData}, Indexes={options.CopyIndexes}, Views={options.CopyViews}, Functions={options.CopyFunctions}, Triggers={options.CopyTriggers}, PlatformSchemas={(options.ExcludePlatformSchemas ? "Excluded" : "Included")}");
            if (!options.CopyIndexes)
                listener.OnLogHint(
                    "Indexes off: only secondary indexes are skipped — primary key indexes are always created with table structures.");

            // Read metadata to update object counts — BEFORE destination is verified
            var model = await sourceMetadata.ReadDatabaseModelAsync(
                            options.ExcludePlatformSchemas,
                            cancellationToken);
            var metadata = BuildMetadata(model);
            listener.OnLogMessage($"Source: {metadata.Summary}");

            // Step 2: Verify destination connection
            listener.OnPhaseChanged(ECopyOperationPhase.CheckingDestinationConnection);
            listener.OnStatusMessageChanged("Checking destination...");
            listener.OnLogMessage(
                $"Checking destination: {destination.Host}:{destination.Port}/{destination.DatabaseName}");

            var destVersion = await destAdmin.TestConnectionAsync(cancellationToken);
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
                $"Destination OK: {destAdmin.ProviderName} {destVersion}");

            // Step 3: Check destination permissions
            listener.OnPhaseChanged(ECopyOperationPhase.CheckingPermissions);
            listener.OnStatusMessageChanged("Verifying destination permissions...");
            var requiredPermissions = EPermissionCheck.Connect | EPermissionCheck.CreateObjects
                                                               | EPermissionCheck.InsertData;
            if (options.CopyMode == ECopyMode.Backup)
                requiredPermissions |= EPermissionCheck.CreateDatabase;
            if (options.CopyMode != ECopyMode.Backup)
                requiredPermissions |= EPermissionCheck.DropObjects;

            var permIssues = await destCleaner.CheckPermissionsAsync(
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
            var destTargetToUse = destTarget;

            if (options.CopyMode == ECopyMode.Backup)
            {
                var backupResult = await CreateBackupDatabaseAsync(
                                       source,
                                       destination,
                                       backupNamePrefix,
                                       destAdmin,
                                       listener,
                                       cancellationToken);
                if (!backupResult.Success)
                {
                    listener.OnPhaseChanged(ECopyOperationPhase.Failed);
                    listener.OnStatusMessageChanged("Failed to create backup database");
                    return backupResult;
                }

                destinationToUse = backupResult.BackupDestination!;
                destTargetToUse = _targetFactory.Create(destinationToUse);
                backupDestination = destinationToUse;
                listener.OnLogMessage(
                    $"Backup database created: {backupResult.BackupDatabaseName}");
            }

            // Step 5: Handle destination cleanup (if not backup mode)
            if (options.CopyMode != ECopyMode.Backup)
            {
                listener.OnPhaseChanged(ECopyOperationPhase.CheckingDestination);
                var destHasData = await adapter.CreateAdmin(destTargetToUse).HasDataAsync(cancellationToken);

                if (destHasData)
                {
                    var selection = tableSelection;
                    var selectionActive = selection is { IsActive: true };
                    var clearAll = !selectionActive;

                    if (selectionActive)
                    {
                        var choiceMessage =
                            $"The destination database '{destinationToUse.DatabaseName}' on {destinationToUse.Host} already contains data, "
                            + "and a TABLE SELECTION is active.\n\n"
                            + "How should the destination be cleaned?";

                        var choice = await _confirmationService.ConfirmSelectionCleanAsync(
                                         "Clean Target Database?",
                                         choiceMessage);

                        if (choice == ESelectionCleanChoice.Cancel)
                        {
                            listener.OnPhaseChanged(ECopyOperationPhase.Cancelled);
                            listener.OnLogMessage("Operation cancelled by user");
                            return new CopyWorkflowResult
                                       {
                                           Success = false,
                                           ErrorMessage = "Operation cancelled by user",
                                           BackupDestination = backupDestination
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

                        var confirm = await _confirmationService.ConfirmAsync(
                                          "Clean Target Database?",
                                          confirmMessage);

                        if (!confirm)
                        {
                            listener.OnPhaseChanged(ECopyOperationPhase.Cancelled);
                            listener.OnLogMessage("Operation cancelled by user");
                            return new CopyWorkflowResult
                                       {
                                           Success = false,
                                           ErrorMessage = "Operation cancelled by user",
                                           BackupDestination = backupDestination
                                       };
                        }
                    }

                    listener.OnPhaseChanged(ECopyOperationPhase.CleaningDestination);
                    var latestCleaner = adapter.CreateCleaner(destTargetToUse);
                    bool cleaned;
                    if (clearAll)
                    {
                        cleaned = await latestCleaner.CleanDatabaseAsync(
                                      msg => listener.OnLogMessage(msg),
                                      cancellationToken);
                    }
                    else
                    {
                        // Selection-scoped clean: resolve from source table list
                        var sourceTables = await sourceMetadata.GetTablesAsync(cancellationToken);
                        var selected = sourceTables
                            .Select(t => new TableId(t.Schema, t.Name))
                            .Where(id => !selection!.IsExcluded(id))
                            .ToList();
                        cleaned = await latestCleaner.CleanTablesAsync(
                                      selected,
                                      msg => listener.OnLogMessage(msg),
                                      cancellationToken);
                    }

                    if (!cleaned)
                    {
                        listener.OnPhaseChanged(ECopyOperationPhase.Failed);
                        listener.OnStatusMessageChanged("Failed to clean destination database");
                        return new CopyWorkflowResult
                                   {
                                       Success = false,
                                       ErrorMessage = clearAll
                                           ? "Failed to clean destination database — see log for details"
                                           : "Selection-scoped cleanup failed or aborted — see log for details",
                                       BackupDestination = backupDestination
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

            // Resolve the user's table selection against the source model and build the
            // request.
            var step = PrepareCopyRequest(model, source, destinationToUse, options, tableSelection);
            listener.OnStageCompleted(step.Stage);

            // A selection that excludes every table fails before the engine is called.
            CopyResult engineResult;
            if (step.FailedResult is not null)
            {
                engineResult = step.FailedResult;
            }
            else
            {
                await using var session = adapter.CreateCopySession();
                engineResult = await session.ExecuteAsync(
                                   step.Request!,
                                   progressHandler,
                                   cancellationToken);
            }

            var result = engineResult with
                         {
                             Warnings = [..step.Warnings, ..engineResult.Warnings],
                             StageResults = [step.Stage, ..engineResult.StageResults]
                         };

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
                           Result = result,
                           BackupDestination = backupDestination
                       };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Copy operation cancelled");
            listener.OnPhaseChanged(ECopyOperationPhase.Cancelled);
            listener.OnStatusMessageChanged("Operation cancelled");
            listener.OnLogMessage("Operation cancelled by user");
            return new CopyWorkflowResult
                       {
                           Success = false,
                           ErrorMessage = "Operation cancelled",
                           BackupDestination = backupDestination
                       };
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
                           Success = false,
                           ErrorMessage = ex.Message,
                           Exception = ex,
                           BackupDestination = backupDestination
                       };
        }
        finally
        {
            listener.OnOperationComplete();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Backup
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<CopyWorkflowResult> CreateBackupDatabaseAsync(
        ConnectionInfo source,
        ConnectionInfo destination,
        string? backupNamePrefix,
        IDatabaseAdmin destAdmin,
        ICopyProgressSink listener,
        CancellationToken cancellationToken)
    {
        listener.OnPhaseChanged(ECopyOperationPhase.CreatingBackupDatabase);
        listener.OnStatusMessageChanged("Creating backup database...");

        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var rawPrefix = !string.IsNullOrWhiteSpace(backupNamePrefix)
                                ? backupNamePrefix
                                : !string.IsNullOrWhiteSpace(source.DatabaseName)
                                    ? source.DatabaseName
                                    : !string.IsNullOrWhiteSpace(source.Username)
                                        ? source.Username
                                        : "backup";

            var resolvedSourceDb = destAdmin.BuildBackupDatabaseName(rawPrefix);
            var backupDbName = $"{resolvedSourceDb}_backup_{timestamp}";

            listener.OnLogMessage($"Backup mode: creating new database = {backupDbName}");

            var created = await destAdmin.CreateDatabaseAsync(
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

            // Build the backup destination connection (same as destination but with the new DB name)
            var backupDestination = new ConnectionInfo(
                destination.Host,
                destination.Port,
                backupDbName,
                destination.Username,
                destination.Password,
                destination.SslMode);

            return new CopyWorkflowResult
                       {
                           Success = true,
                           BackupDatabaseName = backupDbName,
                           BackupDestination = backupDestination
                       };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup database");
            listener.OnLogMessage($"Error creating backup database: {ex.Message}");
            return new CopyWorkflowResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Table selection → request preparation (carried over from the partial workflow)
    // ─────────────────────────────────────────────────────────────────────────

    internal CopyRequestStep PrepareCopyRequest(
        DatabaseModel model,
        ConnectionInfo source,
        ConnectionInfo destination,
        CopyOptions options,
        TableSelectionSpec? tableSelection)
    {
        if (tableSelection is not { IsActive: true })
        {
            return new CopyRequestStep(
                BuildRequest(source, destination, model, options),
                NoSelectionStage,
                [],
                null);
        }

        var totalTables = model.Tables.Count;
        var filtered = _filterApplier.Apply(model, tableSelection);
        var warnings = CollectWarnings(filtered.Report);

        if (totalTables > 0 && filtered.FilteredModel.Tables.Count == 0)
        {
            _logger.LogError(
                "Table selection excluded all {TableCount} tables",
                totalTables);

            return new CopyRequestStep(
                null,
                new StageResult(
                    ECopyStage.ApplyTableFilter,
                    false,
                    TimeSpan.Zero,
                    0,
                    [StageDetail.Failed(
                        "Table selection",
                        "Selection excludes every table in the source database")]),
                warnings,
                new CopyResult(
                    false,
                    TimeSpan.Zero,
                    [],
                    [],
                    [ExcludedEverythingError()],
                    NothingCopied));
        }

        _logger.LogInformation(
            "Table selection applied: {Removed} tables excluded, {Remaining} remain",
            filtered.Report.RemovedTables.Count,
            filtered.FilteredModel.Tables.Count);

        return new CopyRequestStep(
            BuildRequest(source, destination, filtered.FilteredModel, options),
            new StageResult(
                ECopyStage.ApplyTableFilter,
                true,
                TimeSpan.Zero,
                filtered.Report.RemovedTables.Count,
                [
                    StageDetail.Statistic(
                        "Tables excluded", filtered.Report.RemovedTables.Count),
                    StageDetail.Statistic(
                        "Tables remaining", filtered.FilteredModel.Tables.Count)
                ]),
            warnings,
            null);
    }

    private CopyRequest BuildRequest(
        ConnectionInfo source,
        ConnectionInfo destination,
        DatabaseModel model,
        CopyOptions options) =>
        new(
            _targetFactory.Create(source),
            _targetFactory.Create(destination),
            model,
            options);

    private static DatabaseMetadata BuildMetadata(DatabaseModel model) =>
        new(
            Tables: model.Tables.Count,
            Views: model.Views.Count,
            MaterializedViews: model.MaterializedViews.Count,
            Sequences: model.Sequences.Count,
            Functions: model.Functions.Count,
            Triggers: model.Triggers.Count,
            Enums: model.Enums.Count,
            Domains: model.Domains.Count,
            CompositeTypes: model.CompositeTypes.Count,
            Indexes: model.Tables.Sum(t => t.Indexes.Count(i => !i.IsPrimary)),
            Constraints: model.Tables.Sum(t =>
                t.ForeignKeys.Count + t.CheckConstraints.Count + t.UniqueConstraints.Count));

    private static CopyError ExcludedEverythingError() =>
        new(
            StageName: ECopyStage.ApplyTableFilter,
            Kind: EStageMessageKind.Failed,
            ObjectName: null,
            Properties: new Dictionary<string, object>
            {
                [PropKeys.Reason] =
                    "The table selection excludes every table in the source database"
            },
            Exception: null);

    private static IReadOnlyList<CopyWarning> CollectWarnings(TableFilterReport report)
    {
        var warnings = new List<CopyWarning>();

        foreach (var stale in report.StaleExclusions)
        {
            warnings.Add(
                Warning(stale.FullName, "No longer exists in the source database"));
        }

        foreach (var fk in report.DroppedForeignKeys)
        {
            warnings.Add(
                Warning(
                    $"{fk.OwningTable.FullName}.{fk.ConstraintName}",
                    $"Foreign key references excluded table {fk.ReferencedTable.FullName}"));
        }

        foreach (var view in report.SkippedViews)
        {
            warnings.Add(
                Warning(view.FullName, "Depends on an excluded table"));
        }

        foreach (var partition in report.OrphanedPartitions)
        {
            warnings.Add(
                Warning(partition.FullName, "Parent table is excluded"));
        }

        return warnings;
    }

    private static CopyWarning Warning(string objectName, string reason) =>
        new(
            ECopyStage.ApplyTableFilter,
            EStageMessageKind.Skipped,
            objectName,
            new Dictionary<string, object> { [PropKeys.Reason] = reason });
}
