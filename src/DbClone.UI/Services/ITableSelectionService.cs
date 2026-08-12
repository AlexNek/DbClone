using DbClone.Application.Models;
using DbClone.Application.TableFilter;
using DbClone.UI.Models;

namespace DbClone.UI.Services;

/// <summary>
/// Single owner of the active table selection for the source connection.
/// Copy, Compare, and Backup all read the same spec from here.
/// </summary>
public interface ITableSelectionService
{
    /// <summary>Raised whenever the visible selection state changes.</summary>
    event Action? Changed;

    /// <summary>The spec to attach to operations; null when no filtering is active.</summary>
    TableSelectionSpec? OperationSpec { get; }

    /// <summary>True when the active selection differs from its saved preset.</summary>
    bool IsDirty { get; }

    /// <summary>The resolved spec for the current source database (never null).</summary>
    TableSelectionSpec ActiveSpec { get; }

    /// <summary>Id of the active preset, or null when "All Tables" is active.</summary>
    string? ActivePresetId { get; }

    /// <summary>The database the current selection belongs to, or null.</summary>
    DatabaseIdentifier? CurrentDatabase { get; }

    /// <summary>Saved presets for the current database, ordered by name.</summary>
    IReadOnlyList<TableSelectionPreset> Presets { get; }

    /// <summary>
    /// Commits the selection made in the dialog: sets the active spec and
    /// records the last-used preset.
    /// </summary>
    Task ApplyDialogSelectionAsync(string? presetId, IReadOnlySet<TableId> excludedTables);

    /// <summary>
    /// Loads presets and restores the last-used preset when the source
    /// connection changes. Null resets to "All Tables" with no database.
    /// </summary>
    Task LoadForConnectionAsync(SavedConnection? connection);

    /// <summary>Refreshes the preset list after save/rename/delete operations.</summary>
    Task ReloadPresetsAsync();

    /// <summary>Switches the active preset from the dropdown (commits last-used).</summary>
    Task SetActivePresetAsync(string? presetId);
}
