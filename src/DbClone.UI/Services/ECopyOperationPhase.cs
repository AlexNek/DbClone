namespace DbClone.UI.Services;

/// <summary>
/// Represents phases in the copy operation workflow.
/// </summary>
public enum ECopyOperationPhase
{
    Initializing,

    CheckingSourceConnection,

    CheckingDestinationConnection,

    CheckingPermissions,

    CreatingBackupDatabase,

    CheckingDestination,

    CleaningDestination,

    AwaitingUserConfirmation,

    RunningPipeline,

    Completed,

    Failed,

    Cancelled
}
