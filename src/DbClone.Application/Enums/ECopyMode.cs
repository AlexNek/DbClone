namespace DbClone.Application.Enums;

/// <summary>
/// Controls how the copy pipeline handles data and schema.
/// </summary>
public enum ECopyMode
{
    /// <summary>Drop and recreate everything, copy all tables. Use for fresh copies to empty databases.</summary>
    Full,

    /// <summary>Skip DDL stages, compare row counts, only copy missing/mismatched tables. Use to resume after connection failure.</summary>
    Resume,

    /// <summary>Skip DDL stages, compare data content, copy tables that differ. Use to sync changes to existing destination.</summary>
    Update,

    /// <summary>Full copy to a new auto-named database with timestamp. Creates a backup copy to a new DB.</summary>
    Backup
}
