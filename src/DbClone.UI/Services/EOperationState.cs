namespace DbClone.UI.Services;

/// <summary>
/// High-level operation states for the UI state machine.
/// </summary>
public enum EOperationState
{
    Idle,

    Running,

    Completed,

    Failed,

    Cancelled
}
