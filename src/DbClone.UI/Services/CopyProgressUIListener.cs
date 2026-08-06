using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.UI.ViewModels;

namespace DbClone.UI.Services;

/// <summary>
/// Bridges <see cref="ICopyProgressListener"/> events to CopyOperationViewModel UI state.
/// Translates orchestrator callbacks into observable property updates.
/// </summary>
public sealed class CopyProgressUIListener : ICopyProgressListener
{
    private readonly CopyOperationViewModel _copy;

    private readonly WorkflowState _state;

    public CopyProgressUIListener(CopyOperationViewModel copy, WorkflowState state)
    {
        _copy = copy;
        _state = state;
    }

    public void OnError(CopyError error)
    {
        var text = StageDetailRenderer.RenderError(error);
        _state.LastError = $"[{error.StageName.DisplayName()}] {text}";
        _state.LogError($"[{error.StageName.DisplayName()}]: {text}");
    }

    public void OnLogMessage(string message)
    {
        _state.Log(message);
    }

    public void OnLogHint(string message)
    {
        _state.LogHint(message);
    }

    public void OnOperationComplete()
    {
        // Final cleanup is handled by MainViewModel in the finally block
    }

    public void OnPhaseChanged(ECopyOperationPhase phase)
    {
        _copy.CurrentPhase = phase switch
            {
                ECopyOperationPhase.Initializing => "Starting...",
                ECopyOperationPhase.CheckingSourceConnection => "Checking Connections",
                ECopyOperationPhase.CheckingDestinationConnection => "Checking Connections",
                ECopyOperationPhase.CheckingPermissions => "Checking Permissions",
                ECopyOperationPhase.CreatingBackupDatabase => "Creating Backup Database",
                ECopyOperationPhase.CheckingDestination => "Preparing Destination",
                ECopyOperationPhase.CleaningDestination => "Cleaning Destination",
                ECopyOperationPhase.AwaitingUserConfirmation => "Waiting for Confirmation",
                ECopyOperationPhase.RunningPipeline => "Running Pipeline",
                ECopyOperationPhase.Completed => "Complete",
                ECopyOperationPhase.Failed => "Failed",
                ECopyOperationPhase.Cancelled => "Cancelled",
                _ => phase.ToString()
            };
    }

    public void OnProgressChanged(CopyProgress progress)
    {
        _state.StatusMessage =
            $"Stage: {progress.CurrentStage.DisplayName()} ({progress.CompletedStages}/{progress.TotalStages})";
        _copy.ProgressPercent = (int)progress.PercentComplete;
        _state.ElapsedTime = FormatElapsed(progress.ElapsedSeconds);

        _copy.CurrentPhase = progress.CurrentStage switch
            {
                ECopyStage.CopyData => "Copying Data",
                ECopyStage.CreateIndexes => "Creating Indexes",
                ECopyStage.CreateSchemas or ECopyStage.CreateExtensions
                    or ECopyStage.CreateSequences or ECopyStage.CreateTypes
                    or ECopyStage.CreateTables or ECopyStage.ReconcileColumns => "Creating Schema",
                ECopyStage.Validate => "Validating",
                ECopyStage.CreateFunctions or ECopyStage.RetryFunctions or ECopyStage.CreateViews
                    or ECopyStage.CreateTriggers => "Creating Objects",
                _ => progress.CurrentStage.DisplayName()
            };

        _copy.IsCopyingData = progress.CurrentStage == ECopyStage.CopyData;

        // Stage-start reports (CompletedStage = null) mark the matching panel item
        // as in-progress; completion is handled in OnStageCompleted.
        if (progress.CompletedStage is null)
        {
            var panelObject = progress.CurrentStage switch
                {
                    ECopyStage.CreateTables or ECopyStage.ReconcileColumns or ECopyStage.CopyData => EDatabaseObjectType.Table,
                    ECopyStage.CreateIndexes => EDatabaseObjectType.Index,
                    ECopyStage.CreateSequences or ECopyStage.SyncSequences => EDatabaseObjectType
                        .Sequence,
                    ECopyStage.CreateViews => EDatabaseObjectType.View,
                    ECopyStage.CreateFunctions or ECopyStage.RetryFunctions => EDatabaseObjectType
                        .Function,
                    ECopyStage.CreateConstraints => EDatabaseObjectType.Constraint,
                    _ => (EDatabaseObjectType?)null
                };
            if (panelObject is not null)
                _state.ObjectsPanel.SetInProgress(panelObject.Value);
        }

        if (progress.TableProgress is { } tp)
        {
            _copy.CurrentTable = tp.TableName;
            _copy.RowsProcessed = tp.RowsCompleted;
            _copy.TotalRows = tp.TotalRows;
            _copy.TableProgressPercent = tp.TotalRows > 0
                                             ? Math.Round(
                                                 tp.RowsCompleted * 100.0 / tp.TotalRows,
                                                 1)
                                             : 0;

            if (tp.ElapsedSeconds > 0 && tp.RowsCompleted > 0)
            {
                var rowsPerSecond = tp.RowsCompleted / tp.ElapsedSeconds;
                _copy.TransferSpeed = $"{rowsPerSecond:N0} rows/s";

                var remainingRows = tp.TotalRows - tp.RowsCompleted;
                if (remainingRows > 0 && rowsPerSecond > 0)
                {
                    var etaSeconds = remainingRows / rowsPerSecond;
                    _copy.EstimatedTimeRemaining = FormatElapsed(etaSeconds);
                }
                else
                {
                    _copy.EstimatedTimeRemaining = "\u2014";
                }
            }
        }
    }

    public void OnStageCompleted(StageResult stage)
    {
        var status = stage.Success ? "OK" : "FAIL";
        var message =
            $"[{status}] {stage.StageName.DisplayName()}: {stage.ObjectsProcessed} objects in {stage.Duration.TotalSeconds:F1}s";

        if (stage.Success)
            _state.Log(message);
        else
            _state.LogError(message);

        // Each detail carries its own severity, tagged at the source (pipeline stage).
        // A failed stage mixes OK and FAIL lines, so per-detail levels keep the
        // errors-only view accurate.
        foreach (var detail in stage.Details)
            _state.LogDetail(StageDetailRenderer.Render(detail), detail.Level);

        switch (stage.StageName)
        {
            case ECopyStage.ReadMetadata:
                foreach (var detail in stage.Details.Where(d => d.Kind == EStageMessageKind.Count))
                {
                    var objType = detail.Get<EDatabaseObjectType>(PropKeys.ObjectType);
                    var count = detail.Get<int>(PropKeys.Count);
                    _state.ObjectsPanel.SetCount(objType, count);
                }

                break;

            case ECopyStage.CreateTables:
            case ECopyStage.CopyData:
                FinalizePanelObject(EDatabaseObjectType.Table, stage.Success);
                break;

            case ECopyStage.CreateSequences:
            case ECopyStage.SyncSequences:
                FinalizePanelObject(EDatabaseObjectType.Sequence, stage.Success);
                break;

            case ECopyStage.CreateTypes:
                FinalizePanelObject(EDatabaseObjectType.Enum, stage.Success);
                FinalizePanelObject(EDatabaseObjectType.Domain, stage.Success);
                FinalizePanelObject(EDatabaseObjectType.CompositeType, stage.Success);
                break;

            case ECopyStage.CreateViews:
                FinalizePanelObject(EDatabaseObjectType.View, stage.Success);
                break;

            case ECopyStage.CreateFunctions:
            case ECopyStage.RetryFunctions:
                FinalizePanelObject(EDatabaseObjectType.Function, stage.Success);
                break;

            case ECopyStage.CreateIndexes:
                FinalizePanelObject(EDatabaseObjectType.Index, stage.Success);
                break;

            case ECopyStage.CreateTriggers:
                FinalizePanelObject(EDatabaseObjectType.Trigger, stage.Success);
                break;

            case ECopyStage.CreateConstraints:
                FinalizePanelObject(EDatabaseObjectType.Constraint, stage.Success);
                break;
        }
    }

    public void OnStatusMessageChanged(string message)
    {
        _state.StatusMessage = message;
    }

    /// <summary>Marks a panel object as Done or Failed so it never stays stuck in-progress.</summary>
    private void FinalizePanelObject(EDatabaseObjectType objectType, bool success)
    {
        if (success)
            _state.ObjectsPanel.SetDone(objectType);
        else
            _state.ObjectsPanel.SetFailed(objectType);
    }

    private static string FormatElapsed(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

}
