using System.IO;
using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.Interchange;
using DbClone.Application.TableFilter;
using DbClone.UI.Models;
using DbClone.UI.Services;
using DbClone.UI.Settings;

using Microsoft.Extensions.Logging;

using Wpf.Ui.Controls;

namespace DbClone.UI.ViewModels.Workspaces.StoreRestore;

/// <summary>
/// Workspace ViewModel for the Store / Restore feature.
/// Implements <see cref="IWorkflowViewModel"/> so it joins the existing toolbar,
/// log pane, and shortcut infrastructure alongside Copy and Compare.
/// </summary>
/// <remarks>
/// All product calls go through <see cref="IInterchangeService"/>; the ViewModel never
/// touches the interchange product types directly. The source panel connection is the
/// store origin, the destination panel connection is the restore target.
/// </remarks>
public sealed partial class StoreRestoreViewModel : ObservableObject, IWorkflowViewModel
{
    private readonly IInterchangeService _interchangeService;
    private readonly IDialogService _dialogService;
    private readonly ITableSelectionService _tableSelectionService;
    private readonly ILogger<StoreRestoreViewModel> _logger;
    private readonly SettingsPersistenceManager _settingsPersister;
    private readonly UserSettings _settings;
    private readonly OperationContext _ctx;

    private CancellationTokenSource? _operationCts;

    /// <summary>Direction switch: Store (database → catalog) or Restore (catalog → database).</summary>
    [ObservableProperty]
    private bool _isStoreDirection = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    /// <summary>Per-workflow UI state (logs, banner, status, objects panel).</summary>
    public WorkflowState State { get; } = new();

    /// <inheritdoc />
    bool IWorkflowViewModel.IsRunning => IsRunning;

    /// <inheritdoc />
    ICommand IWorkflowViewModel.PauseCommand => PauseCommand;

    /// <inheritdoc />
    ICommand IWorkflowViewModel.StopCommand => StopCommand;

    public StoreRestoreViewModel(
        IInterchangeService interchangeService,
        IDialogService dialogService,
        ITableSelectionService tableSelectionService,
        SettingsPersistenceManager settingsPersister,
        UserSettings settings,
        OperationContext context,
        ILogger<StoreRestoreViewModel> logger)
    {
        _interchangeService = interchangeService ?? throw new ArgumentNullException(nameof(interchangeService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _tableSelectionService = tableSelectionService ?? throw new ArgumentNullException(nameof(tableSelectionService));
        _settingsPersister = settingsPersister ?? throw new ArgumentNullException(nameof(settingsPersister));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Switches the workspace to Store direction (database → catalog).</summary>
    [RelayCommand]
    private void SwitchToStore()
    {
        IsStoreDirection = true;
        _logger.LogInformation("Switched to Store direction");
    }

    /// <summary>Switches the workspace to Restore direction (catalog → database).</summary>
    [RelayCommand]
    private void SwitchToRestore()
    {
        IsStoreDirection = false;
        _logger.LogInformation("Switched to Restore direction");
    }

    /// <summary>
    /// Starts a store operation (database → catalog).
    /// Shows a folder browser dialog to select/create the catalog base directory, then
    /// delegates the export to <see cref="IInterchangeService"/>.
    /// </summary>
    [RelayCommand]
    private async Task StartStoreAsync(CancellationToken cancellationToken)
    {
        IsStoreDirection = true;
        _settingsPersister.SaveNow();

        // Store needs a concrete source database — a server-only connection is not enough.
        if (string.IsNullOrWhiteSpace(_ctx.Source.DatabaseName))
        {
            State.BeginNewRun();
            State.LogError(
                "Source connection has no database name. Store requires a specific database.");
            State.ShowBanner(
                "Store blocked",
                "The source connection has no database name — select a specific database to store.",
                InfoBarSeverity.Error);
            return;
        }

        // Show folder browser dialog to let user select catalog directory
        var options = _settings.StoreRestore;
        var selectedDirectory = _dialogService.BrowseFolder(
            "Select Catalog Directory",
            options.DefaultCatalogDirectory);

        if (string.IsNullOrWhiteSpace(selectedDirectory))
        {
            _logger.LogInformation("Store cancelled: no directory selected");
            return;
        }

        // Ensure the selected directory exists
        try
        {
            if (!Directory.Exists(selectedDirectory))
            {
                Directory.CreateDirectory(selectedDirectory);
                _logger.LogInformation("Created catalog directory: {Directory}", selectedDirectory);
            }

            // Update the default directory for next time
            options.DefaultCatalogDirectory = selectedDirectory;
            _settingsPersister.SaveNow();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create catalog directory: {Directory}", selectedDirectory);
            State.LogError($"Failed to create directory: {ex.Message}");
            return;
        }

        var catalogPath = ResolveCatalogPath(
            selectedDirectory, options.CatalogSubfolderPattern, _ctx.Source.DatabaseName);

        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _operationCts.Token;

        _logger.LogInformation("Store operation started: catalog={CatalogPath}", catalogPath);
        _ctx.IsBusy = true;
        IsRunning = true;
        State.BeginNewRun();
        State.Log($"Store: {_ctx.Source.Summary} → {catalogPath}");
        State.LogDetail($"Include data: {options.IncludeData}, chunk: {options.ChunkBytes:N0} bytes, rows/chunk: {options.RowsPerChunk:N0}");

        var tableSelection = _tableSelectionService.OperationSpec;
        if (tableSelection is not null)
            State.LogDetail($"Table selection active: {tableSelection.ExcludedTables.Count} table(s) excluded");

        try
        {
            var request = new InterchangeStoreRequest(
                Connection: ConnectionInfoFactory.FromViewModel(_ctx.Source),
                TableSelection: tableSelection ?? TableSelectionSpec.All,
                CatalogPath: catalogPath,
                ChunkBytes: options.ChunkBytes,
                RowsPerChunk: options.RowsPerChunk,
                IncludeData: options.IncludeData,
                SnapshotConsistency: options.SnapshotConsistency,
                UnsupportedFeaturePolicy: options.UnsupportedFeaturePolicy,
                PackAfterStore: options.PackAfterStore,
                VerifyAfterStore: true);

            var result = await _interchangeService.StoreAsync(request, CreateProgressReporter(), ct);
            ReportStoreResult(result);
        }
        catch (OperationCanceledException)
        {
            State.Log("Store cancelled by user.");
            State.StatusBarSummary = "Store cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Store operation failed");
            State.LastError = ex.Message;
            State.LogError($"Store failed: {ex.Message}");
            State.StatusBarSummary = $"Store FAILED: {ex.Message}";
        }
        finally
        {
            _ctx.IsBusy = false;
            IsRunning = false;
            IsPaused = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>
    /// Starts a restore operation (catalog → database).
    /// Shows a folder browser dialog to select the catalog directory, plans the import,
    /// then delegates to <see cref="IInterchangeService"/>.
    /// </summary>
    [RelayCommand]
    private async Task StartRestoreAsync(CancellationToken cancellationToken)
    {
        IsStoreDirection = false;
        _settingsPersister.SaveNow();

        // Restore needs a concrete target database — a server-only connection is not enough.
        if (string.IsNullOrWhiteSpace(_ctx.Destination.DatabaseName))
        {
            State.BeginNewRun();
            State.LogError(
                "Destination connection has no database name. Restore requires a specific database.");
            State.ShowBanner(
                "Restore blocked",
                "The destination connection has no database name — select a specific database to restore into.",
                InfoBarSeverity.Error);
            return;
        }

        // Show folder browser dialog to let user select catalog directory
        var options = _settings.StoreRestore;
        var selectedDirectory = _dialogService.BrowseFolder(
            "Select Catalog Directory to Restore",
            options.DefaultCatalogDirectory);

        if (string.IsNullOrWhiteSpace(selectedDirectory))
        {
            _logger.LogInformation("Restore cancelled: no directory selected");
            return;
        }

        // Verify the selected directory is an actual catalog
        if (!Directory.Exists(selectedDirectory))
        {
            _logger.LogWarning("Selected catalog directory does not exist: {Directory}", selectedDirectory);
            State.LogError($"Selected directory does not exist: {selectedDirectory}");
            return;
        }

        // The browsed folder must be the catalog itself (a folder produced by Store);
        // the product validates it and reports a clear error when it is not.
        var catalogPath = selectedDirectory;

        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _operationCts.Token;

        _logger.LogInformation("Restore operation started: catalog={CatalogPath}", catalogPath);
        _ctx.IsBusy = true;
        IsRunning = true;
        State.BeginNewRun();
        State.Log($"Restore: {catalogPath} → {_ctx.Destination.Summary}");
        State.LogDetail($"Unsupported feature policy: {options.UnsupportedFeaturePolicy}");

        try
        {
            // Read-only plan first: it reports what the catalog contains before anything is applied
            var plan = await _interchangeService.PlanRestoreAsync(catalogPath, ct);
            State.LogDetail(
                $"Plan: {plan.ObjectCount} object(s) across {plan.Stages} stage(s)");
            foreach (var refused in plan.RefusedObjects)
                State.LogWarning($"Refused: {refused}");

            var request = new InterchangeRestoreRequest(
                CatalogPath: catalogPath,
                Connection: ConnectionInfoFactory.FromViewModel(_ctx.Destination),
                UnsupportedFeaturePolicy: options.UnsupportedFeaturePolicy);

            var result = await _interchangeService.RestoreAsync(request, CreateProgressReporter(), ct);
            ReportRestoreResult(result);
        }
        catch (OperationCanceledException)
        {
            State.Log("Restore cancelled by user.");
            State.StatusBarSummary = "Restore cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore operation failed");
            State.LastError = ex.Message;
            State.LogError($"Restore failed: {ex.Message}");
            State.StatusBarSummary = $"Restore FAILED: {ex.Message}";
        }
        finally
        {
            _ctx.IsBusy = false;
            IsRunning = false;
            IsPaused = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>
    /// Builds the catalog directory for one store run: the browsed base folder plus the
    /// <see cref="StoreRestoreOptions.CatalogSubfolderPattern"/> subfolder. An empty pattern
    /// stores directly into the base folder.
    /// </summary>
    private static string ResolveCatalogPath(string baseDirectory, string pattern, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return baseDirectory;

        var folder = pattern
            .Replace("{Database}", databaseName, StringComparison.OrdinalIgnoreCase)
            .Replace("{timestamp}", DateTime.Now.ToString("yyyy-MM-dd_HH-mm"), StringComparison.OrdinalIgnoreCase);

        foreach (var invalid in Path.GetInvalidFileNameChars())
            folder = folder.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(folder)
            ? baseDirectory
            : Path.Combine(baseDirectory, folder.Trim());
    }

    /// <summary>
    /// Progress sink for the product: mirrors the current stage into the status line and
    /// writes a log line only on a stage or object boundary, so the log stays readable.
    /// </summary>
    private IProgress<InterchangeProgressView> CreateProgressReporter()
    {
        string? lastStage = null;
        string? lastObject = null;

        return new Progress<InterchangeProgressView>(p =>
        {
            State.StatusMessage = p.Stage;

            if (p.Stage != lastStage)
            {
                lastStage = p.Stage;
                lastObject = null;
                State.Log($"{p.Stage} ({p.TableOrdinal}/{p.TableTotal})");
            }

            if (!string.IsNullOrEmpty(p.CurrentTable) && p.CurrentTable != lastObject)
            {
                lastObject = p.CurrentTable;
                State.LogDetail(p.CurrentTable);
            }
        });
    }

    /// <summary>Logs every issue reported by the product, one line per issue.</summary>
    private void ReportIssues(IReadOnlyList<InterchangeIssueView> issues)
    {
        foreach (var issue in issues)
        {
            var location = string.IsNullOrEmpty(issue.Location) ? string.Empty : $" [{issue.Location}]";
            var message = $"{issue.Code}: {issue.Sentence}{location}";
            if (issue.Severity == EInterchangeSeverity.Error)
                State.LogError(message);
            else
                State.LogWarning(message);

            if (!string.IsNullOrEmpty(issue.Details))
                State.LogDetail(issue.Details);
        }
    }

    /// <summary>Turns a store result into log lines, banner and status-bar summary.</summary>
    private void ReportStoreResult(InterchangeStoreResult result)
    {
        ReportIssues(result.Issues);

        var catalogPath = result.CatalogPath ?? string.Empty;
        switch (result.Outcome)
        {
            case EStoreOutcome.Success:
            case EStoreOutcome.CompletedWithIssues:
                State.StatusBarSummary =
                    $"Store succeeded — {result.TablesExported} tables, {result.RowsExported:N0} rows "
                    + $"in {result.Elapsed.TotalSeconds:F1}s";
                State.ShowBanner(
                    "Store Complete",
                    $"{result.TablesExported} tables, {result.RowsExported:N0} rows exported to {catalogPath} "
                    + $"in {result.Elapsed.TotalSeconds:F1}s",
                    result.Outcome == EStoreOutcome.CompletedWithIssues
                        ? InfoBarSeverity.Warning
                        : InfoBarSeverity.Success);
                State.Log(result.Outcome == EStoreOutcome.CompletedWithIssues
                    ? "=== STORE SUCCEEDED WITH ISSUES ==="
                    : "=== STORE SUCCEEDED ===");
                break;

            case EStoreOutcome.Incomplete:
                State.StatusBarSummary = "Store incomplete — the catalog must be re-created";
                State.ShowBanner(
                    "Store Incomplete",
                    "The store was cancelled before finishing; the catalog is not usable. Run Store again to create it from scratch.",
                    InfoBarSeverity.Warning);
                State.LogWarning("Store stopped before completion.");
                break;

            default:
                State.StatusBarSummary = "Store FAILED";
                State.LogError("=== STORE FAILED ===");
                break;
        }

        if (!string.IsNullOrEmpty(catalogPath))
            State.LogDetail($"Catalog: {catalogPath}");
        if (!string.IsNullOrEmpty(result.PackedZipPath))
            State.LogDetail($"Packed archive: {result.PackedZipPath}");
        if (!result.VerificationPassed && result.Outcome != EStoreOutcome.Failed)
            State.LogWarning("Catalog verification did not pass.");
    }

    /// <summary>Turns a restore result into log lines, banner and status-bar summary.</summary>
    private void ReportRestoreResult(InterchangeRestoreResult result)
    {
        ReportIssues(result.Issues);

        switch (result.Outcome)
        {
            case ERestoreOutcome.Success:
            case ERestoreOutcome.CompletedWithIssues:
                State.StatusBarSummary =
                    $"Restore succeeded — {result.ObjectsApplied} object(s) applied "
                    + $"in {result.Elapsed.TotalSeconds:F1}s";
                State.ShowBanner(
                    "Restore Complete",
                    $"{result.ObjectsApplied} object(s) applied in {result.Elapsed.TotalSeconds:F1}s",
                    result.Outcome == ERestoreOutcome.CompletedWithIssues
                        ? InfoBarSeverity.Warning
                        : InfoBarSeverity.Success);
                State.Log(result.Outcome == ERestoreOutcome.CompletedWithIssues
                    ? "=== RESTORE SUCCEEDED WITH ISSUES ==="
                    : "=== RESTORE SUCCEEDED ===");
                break;

            case ERestoreOutcome.Cancelled:
                State.StatusBarSummary = "Restore cancelled";
                State.LogWarning($"Restore cancelled after {result.ObjectsApplied} object(s).");
                if (!string.IsNullOrEmpty(result.LastCompletedChunk))
                    State.LogHint($"Resume point: {result.LastCompletedChunk}");
                break;

            case ERestoreOutcome.Refused:
                State.StatusBarSummary = "Restore refused";
                State.ShowBanner(
                    "Restore Refused",
                    "The catalog was refused before any changes were applied — the target database was left untouched.",
                    InfoBarSeverity.Warning);
                State.LogError("=== RESTORE REFUSED (no changes applied) ===");
                break;

            default:
                State.StatusBarSummary = "Restore FAILED";
                State.LogError("=== RESTORE FAILED ===");
                break;
        }

        if (result.RowCountMismatches > 0)
            State.LogWarning($"{result.RowCountMismatches} table(s) have a row-count mismatch.");
    }

    /// <summary>Pauses or resumes the running operation.</summary>
    [RelayCommand]
    private void Pause()
    {
        if (!IsRunning) return;
        IsPaused = !IsPaused;
        State.Log(IsPaused ? "Operation paused" : "Operation resumed");
    }

    /// <summary>Stops the running operation via cancellation token.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (_operationCts is { IsCancellationRequested: false })
        {
            State.Log("Stop requested — cancelling operation...");
            _operationCts.Cancel();
        }
    }
}
