using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Persistence for named table selection presets, keyed by
/// <see cref="DatabaseIdentifier"/> (saved connection profile + database name).
/// </summary>
public interface ITableSelectionPresetStore
{
    /// <summary>Deletes the preset from the given database.</summary>
    Task DeletePresetAsync(DatabaseIdentifier database, string presetId);

    /// <summary>Returns the id of the last-used preset, or null when none was recorded.</summary>
    Task<string?> GetLastUsedPresetIdAsync(DatabaseIdentifier database);

    /// <summary>Returns a single preset, or null when it does not exist.</summary>
    Task<TableSelectionPreset?> GetPresetAsync(DatabaseIdentifier database, string presetId);

    /// <summary>Returns all presets for the given database, ordered by name.</summary>
    Task<IReadOnlyList<TableSelectionPreset>> LoadPresetsAsync(DatabaseIdentifier database);

    /// <summary>Renames an existing preset. The new name must pass validation.</summary>
    Task RenamePresetAsync(DatabaseIdentifier database, string presetId, string newName);

    /// <summary>Creates or updates a preset.</summary>
    Task SavePresetAsync(DatabaseIdentifier database, TableSelectionPreset preset);

    /// <summary>Records the last-used preset. Null clears the record.</summary>
    Task SetLastUsedPresetIdAsync(DatabaseIdentifier database, string? presetId);
}
