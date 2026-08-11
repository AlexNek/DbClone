using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Validates table selection preset names against the persistence rules:
/// non-empty, max length, the reserved "All Tables" name, and
/// case-insensitive uniqueness per database.
/// </summary>
public interface ITableSelectionPresetNameValidator
{
    /// <summary>
    /// Returns a user-facing error message, or null when the name is valid.
    /// When <paramref name="excludePresetId"/> is given (rename), the preset's own
    /// current name does not count as a duplicate.
    /// </summary>
    string? Validate(
        string name,
        IEnumerable<TableSelectionPreset> existingPresets,
        string? excludePresetId = null);
}
