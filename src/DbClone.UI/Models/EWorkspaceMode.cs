namespace DbClone.UI.Models;

/// <summary>
/// Active workspace mode — drives toolbar visibility and content area switching.
/// Store/Restore is not a mode: it is an operation triggered from the connection
/// panels and runs alongside whichever mode (Copy / Compare) is active.
/// </summary>
public enum EWorkspaceMode
{
    Copy,
    Compare
}
