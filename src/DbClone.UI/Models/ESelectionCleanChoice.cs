namespace DbClone.UI.Models;

/// <summary>
/// User's choice when a populated destination meets an active table selection:
/// replace only the selected tables, clear the destination down to the selection,
/// or cancel the copy entirely.
/// </summary>
public enum ESelectionCleanChoice
{
    /// <summary>Cancel the copy operation.</summary>
    Cancel,

    /// <summary>Drop only the selected tables on the destination; all other tables remain untouched.</summary>
    ReplaceSelectedOnly,

    /// <summary>Drop every table on the destination; afterwards it contains only the selected tables.</summary>
    ClearEntireDestination
}
