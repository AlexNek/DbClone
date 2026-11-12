namespace DbClone.Application.Interfaces;

/// <summary>Choice made before restoring into a populated destination database.</summary>
public enum ERestoreTargetChoice
{
    /// <summary>Abort without changing or creating a database.</summary>
    Cancel,

    /// <summary>Remove user content from the selected database, then restore into it.</summary>
    UpdateSelected,

    /// <summary>Create a new database and restore into that database.</summary>
    CreateNew,
}
