namespace DbClone.Application.Enums;

/// <summary>
/// Strategy for handling constraints during copy.
/// </summary>
public enum EConstraintStrategy
{
    /// <summary>Automatically select based on server capabilities.</summary>
    Automatic,

    /// <summary>Use session_replication_role to disable triggers.</summary>
    SessionReplicationRole,

    /// <summary>Disable triggers individually.</summary>
    DisableTriggers,

    /// <summary>Defer all constraints.</summary>
    DeferConstraints,

    /// <summary>Drop and recreate foreign keys.</summary>
    RecreateForeignKeys
}
