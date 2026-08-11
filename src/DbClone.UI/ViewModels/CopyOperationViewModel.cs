using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.UI.Services;
using DbClone.UI.Settings;

using Microsoft.Extensions.Logging;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Owns all copy progress state and execution logic.
/// Exposed as MainViewModel.Copy; ProgressView binds to it.
/// </summary>
public sealed partial class CopyOperationViewModel : ObservableObject, IWorkflowViewModel
{
    private readonly ICopyEngine _copyEngine;

    private readonly OperationContext _ctx;

    private readonly ILogger<CopyOperationViewModel> _logger;

    private readonly CopyOperationOrchestrator _orchestrator;

    private readonly ManualResetEventSlim _pauseGate = new(true);

    private readonly SettingsPersistenceManager _settingsPersister;

    private readonly ViewModelStateManager _stateManager;

    private readonly ITableSelectionService _tableSelectionService;

    [ObservableProperty]
    private string _currentPhase = "Ready";

    [ObservableProperty]
    private string _currentTable = "\u2014";

    [ObservableProperty]
    private string _estimatedTimeRemaining = "\u2014";

    [ObservableProperty]
    private bool _isCopyingData;

    [ObservableProperty]
    private bool _isCopyRunning;

    [ObservableProperty]
    private bool _isPaused;

    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private long _rowsProcessed;

    [ObservableProperty]
    private double _tableProgressPercent;

    [ObservableProperty]
    private long _totalRows;

    [ObservableProperty]
    private string _transferSpeed = "\u2014";

    public bool CanStartCopy => !_ctx.IsBusy;

    /// <summary>Always true for copy — shows Speed/ETA in ProgressView.</summary>
    public bool IsDataTransferStatsVisible => true;

    /// <summary>Whether the copy operation is currently running (IWorkflowViewModel).</summary>
    public bool IsRunning => IsCopyRunning;

    /// <summary>Per-workflow UI state (logs, banner, status, objects panel).</summary>
    public WorkflowState State { get; }

    ICommand IWorkflowViewModel.PauseCommand => PauseCommand;

    ICommand IWorkflowViewModel.StopCommand => StopCommand;

    /// <summary>Pause gate for the copy pipeline to block on.</summary>
    public ManualResetEventSlim PauseGate => _pauseGate;

    public UserSettings Settings { get; }

    public CopyOperationViewModel(
        ILogger<CopyOperationViewModel> logger,
        ICopyEngine copyEngine,
        CopyOperationOrchestrator orchestrator,
        SettingsPersistenceManager settingsPersister,
        ViewModelStateManager stateManager,
        ITableSelectionService tableSelectionService,
        UserSettings settings,
        OperationContext ctx)
    {
        _logger = logger;
        _copyEngine = copyEngine;
        _orchestrator = orchestrator;
        _settingsPersister = settingsPersister;
        _stateManager = stateManager;
        _tableSelectionService = tableSelectionService;
        Settings = settings;
        _ctx = ctx;
        State = new WorkflowState();
    }

    /// <summary>Resets all copy progress state to its pristine pre-run values.</summary>
    private void ResetProgress()
    {
        CurrentPhase = "Ready";
        ProgressPercent = 0;
        TableProgressPercent = 0;
        RowsProcessed = 0;
        TotalRows = 0;
        IsCopyingData = false;
        CurrentTable = "\u2014";
        TransferSpeed = "\u2014";
        EstimatedTimeRemaining = "\u2014";
    }

    [RelayCommand]
    private void Pause()
    {
        if (!_ctx.IsBusy) return;

        if (_pauseGate.IsSet)
        {
            _pauseGate.Reset();
            IsPaused = true;
            State.Log("Operation paused");
        }
        else
        {
            _pauseGate.Set();
            IsPaused = false;
            State.Log("Operation resumed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartCopy))]
    private async Task StartCopyAsync(CancellationToken cancellationToken)
    {
        // A copy needs a concrete database on both sides — except Backup mode,
        // which auto-creates a new database on the destination server.
        if (string.IsNullOrWhiteSpace(_ctx.Source.DatabaseName))
        {
            State.BeginNewRun();
            State.LogError(
                "Source connection has no database name. Copy requires a specific database.");
            State.ShowBanner(
                "Copy blocked",
                "The source connection has no database name — select a specific database to copy from.",
                Wpf.Ui.Controls.InfoBarSeverity.Error);
            return;
        }

        if (Settings.SelectedCopyMode != ECopyMode.Backup
            && string.IsNullOrWhiteSpace(_ctx.Destination.DatabaseName))
        {
            State.BeginNewRun();
            State.LogError(
                "Destination connection has no database name. "
                + $"{Settings.SelectedCopyMode} mode requires a specific database — "
                + "backup-only connections can only be used in Backup mode.");
            State.ShowBanner(
                "Copy blocked",
                "The destination connection has no database name. Choose Backup mode, or enter a database name on the destination connection.",
                Wpf.Ui.Controls.InfoBarSeverity.Error);
            return;
        }

        // Resume/Update replay against already-copied state and require the
        // full table set — a filtered resume could skip already-copied tables or
        // leave partial tables inconsistent.
        var tableSelection = _tableSelectionService.OperationSpec;
        if (tableSelection is not null
            && Settings.SelectedCopyMode is ECopyMode.Resume or ECopyMode.Update)
        {
            State.BeginNewRun();
            State.LogError(
                $"{Settings.SelectedCopyMode} mode requires the \"All Tables\" selection. "
                + "Resume and Update replay against previously copied tables and cannot run with a filtered table selection.");
            State.LogHint(
                "Switch the source panel table selection back to \"All Tables\", or use Full/Backup mode with the current selection.");
            State.ShowBanner(
                $"{Settings.SelectedCopyMode} is not available with a table selection",
                "Switch back to \"All Tables\" to use Resume/Update, or choose Full/Backup mode.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        _settingsPersister.SaveNow();

        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _operationCts.Token;

        _logger.LogInformation("Copy operation started");
        _ctx.IsBusy = true;
        IsCopyRunning = true;
        State.BeginNewRun();
        ResetProgress();
        CurrentPhase = "Starting...";
        State.Log($"Starting copy: {_ctx.Source.Summary} → {_ctx.Destination.Summary}");
        if (tableSelection is not null)
            State.Log(
                $"Table selection active: {tableSelection.ExcludedTables.Count} table(s) excluded via preset selection");

        _stateManager.BeginOperation();

        try
        {
            var request = new CopyRequest(
                Source: ConnectionInfoFactory.FromViewModel(_ctx.Source),
                Destination: ConnectionInfoFactory.FromViewModel(_ctx.Destination),
                Options: new CopyOptions(
                    CopyData: Settings.CopyData,
                    CopyIndexes: Settings.CopyIndexes,
                    CopyConstraints: true,
                    CopyFunctions: Settings.CopyFunctions,
                    CopyTriggers: Settings.CopyTriggers,
                    CopyViews: Settings.CopyViews,
                    CopySequences: true,
                    CopyMode: Settings.SelectedCopyMode,
                    VerifyMode: Settings.SelectedVerifyMode,
                    ExcludePlatformSchemas: !Settings.CopyPlatformSchemas,
                    TableSelection: tableSelection));

            var listener = new CopyProgressUIListener(this, State);

            var workflowResult = await _orchestrator.ExecuteAsync(
                                     _ctx.Source,
                                     _ctx.Destination,
                                     Settings.SelectedCopyMode,
                                     request,
                                     listener,
                                     ct);

            ProgressPercent = 100;
            IsCopyingData = false;

            if (workflowResult.Result is { } result)
            {
                foreach (var warning in result.Warnings)
                    State.LogWarning(StageDetailRenderer.RenderWarning(warning));
            
                if (result.Success)
                {
                    var warningNote = result.Warnings.Count > 0
                                          ? $" with {result.Warnings.Count} warning(s)"
                                          : "";
                    State.StatusBarSummary =
                        $"Copy succeeded{warningNote} — {result.Statistics.TablesCopied} tables, {result.Statistics.TotalRowsCopied:N0} rows in {result.TotalDuration.TotalSeconds:F1}s";
                    State.ShowBanner(
                        "Copy Complete",
                        $"{result.Statistics.TablesCopied} tables, {result.Statistics.TotalRowsCopied:N0} rows copied in {result.TotalDuration.TotalSeconds:F1}s{warningNote}",
                        result.Warnings.Count > 0
                            ? Wpf.Ui.Controls.InfoBarSeverity.Warning
                            : Wpf.Ui.Controls.InfoBarSeverity.Success);
                    State.Log($"=== COPY SUCCEEDED{warningNote.ToUpperInvariant()} ===");
                    State.LogDetail($"Tables: {result.Statistics.TablesCopied}");
                    State.LogDetail($"Rows: {result.Statistics.TotalRowsCopied}");
                    State.LogDetail($"Views: {result.Statistics.ViewsCopied}");
                    State.LogDetail($"Functions: {result.Statistics.FunctionsCopied}");
                    if (result.Warnings.Count > 0)
                    {
                        foreach (var w in result.Warnings)
                            State.LogDetail(
                                StageDetailRenderer.RenderWarningSummary(w),
                                ELogLevel.Warning);
                    }
                }
                else
                {
                    var failedStages = result.Errors.Select(e => e.StageName).Distinct().ToList();
                    var stageList = string.Join(", ", failedStages.Take(4).Select(s => s.DisplayName()));
                    if (failedStages.Count > 4)
                        stageList += $" +{failedStages.Count - 4} more";
            
                    State.StatusBarSummary = $"Copy FAILED — {stageList}";
                    State.LastError = workflowResult.ErrorMessage ?? "Unknown error";
                    State.LogError(
                        $"=== COPY FAILED: {result.Errors.Count} error(s) in: {stageList} ===");
                }
            }
            else if (!workflowResult.Success)
            {
                State.StatusBarSummary = $"FAILED: {workflowResult.ErrorMessage}";
                State.LastError = workflowResult.ErrorMessage ?? "Unknown error";
            }
        }
        catch (OperationCanceledException)
        {
            CurrentPhase = "Cancelled";
            State.StatusMessage = "Cancelled";
            State.Log("Operation cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy operation failed");
            CurrentPhase = "Error";
            State.StatusMessage = $"Failed: {ex.Message}";
            State.LastError = ex.Message;
            State.Log($"ERROR: {ex.Message}");
            if (ex.InnerException != null)
                State.Log($"Inner: {ex.InnerException.Message}");
        }
        finally
        {
            _stateManager.EndOperation(
                CurrentPhase == "Cancelled" ? EOperationState.Cancelled :
                CurrentPhase == "Error" || CurrentPhase == "Failed" ? EOperationState.Failed :
                EOperationState.Completed);
            _ctx.IsBusy = false;
            IsCopyRunning = false;
            IsPaused = false;
            _pauseGate.Set();
            IsCopyingData = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

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
