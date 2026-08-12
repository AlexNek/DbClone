using DbClone.Application.Interfaces;
using DbClone.Application.Models;

namespace DbClone.Application.TableFilter;

/// <summary>
/// Validates preset names against the persistence rules: non-empty, max length,
/// the reserved "All Tables" name, and case-insensitive uniqueness per database.
/// Stateless — safe to register as a singleton.
/// </summary>
public sealed class TableSelectionPresetNameValidator : ITableSelectionPresetNameValidator
{
    /// <summary>Maximum allowed preset name length.</summary>
    public const int MaxLength = 100;

    /// <summary>The built-in default selection name — user presets cannot use it.</summary>
    public const string ReservedAllTablesName = "All Tables";

    /// <summary>
    /// Returns a user-facing error message, or null when the name is valid.
    /// When <paramref name="excludePresetId"/> is given (rename), the preset's own
    /// current name does not count as a duplicate.
    /// </summary>
    public string? Validate(
        string name,
        IEnumerable<TableSelectionPreset> existingPresets,
        string? excludePresetId = null)
    {
        var trimmed = name.Trim();

        if (trimmed.Length == 0)
        {
            return "Preset name must not be empty.";
        }

        if (trimmed.Length > MaxLength)
        {
            return $"Preset name must not exceed {MaxLength} characters.";
        }

        if (string.Equals(trimmed, ReservedAllTablesName, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{ReservedAllTablesName}' is reserved for the built-in default selection.";
        }

        var duplicate = existingPresets.Any(p =>
            p.Id != excludePresetId
            && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        return duplicate
            ? $"A preset named '{trimmed}' already exists for this database."
            : null;
    }
}
