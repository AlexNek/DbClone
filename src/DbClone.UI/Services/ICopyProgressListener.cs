using DbClone.Application.DTOs;

namespace DbClone.UI.Services;

/// <summary>
/// Observer interface for copy operation progress and lifecycle events.
/// Decouples the orchestrator from direct UI state manipulation.
/// </summary>
public interface ICopyProgressListener
{
    /// <summary>Called when an error occurs during the operation.</summary>
    void OnError(CopyError error);

    /// <summary>Called for log-worthy messages from the orchestrator.</summary>
    void OnLogMessage(string message);

    /// <summary>Called for explanatory hints about behavior or configuration.</summary>
    void OnLogHint(string message);

    /// <summary>Called when the entire operation finishes (success, failure, or cancellation).</summary>
    void OnOperationComplete();

    /// <summary>Called when the operation phase changes (e.g. Initializing → CheckingSource → RunningPipeline).</summary>
    void OnPhaseChanged(ECopyOperationPhase phase);

    /// <summary>Called with incremental copy progress updates.</summary>
    void OnProgressChanged(CopyProgress progress);

    /// <summary>Called when a pipeline stage completes.</summary>
    void OnStageCompleted(StageResult result);

    /// <summary>Called when there is a status message update.</summary>
    void OnStatusMessageChanged(string message);
}
