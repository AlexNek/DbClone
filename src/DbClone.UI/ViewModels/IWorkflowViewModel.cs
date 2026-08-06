using System.Windows.Input;

namespace DbClone.UI.ViewModels;

/// <summary>
/// Common contract of the two workflow components (Copy and Compare).
/// Lets app-level code — elapsed-time routing, pause/stop dispatch, active-workflow
/// selection — treat both workflows symmetrically instead of branching on type.
/// A third workflow only needs to implement this interface to join the app.
/// </summary>
public interface IWorkflowViewModel
{
    /// <summary>Whether this workflow's operation is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Command that pauses the running operation.</summary>
    ICommand PauseCommand { get; }

    /// <summary>Per-workflow UI state (logs, banner, status, objects panel, layout).</summary>
    WorkflowState State { get; }

    /// <summary>Command that stops the running operation.</summary>
    ICommand StopCommand { get; }
}
