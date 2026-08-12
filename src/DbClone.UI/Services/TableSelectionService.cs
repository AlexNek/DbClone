using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.TableFilter;
using DbClone.UI.Models;

using Serilog;

namespace DbClone.UI.Services;

/// <summary>
/// Default implementation of <see cref="ITableSelectionService"/>.
/// Keeps the active spec in memory; presets and the last-used marker
/// are persisted through <see cref="ITableSelectionPresetStore"/>.
/// </summary>
public sealed class TableSelectionService : ITableSelectionService
{
    private readonly ITableSelectionPresetStore _presetStore;

    private TableSelectionSpec _activeSpec = TableSelectionSpec.All;

    private string? _activePresetId;

    private DatabaseIdentifier? _currentDatabase;

    private bool _isDirty;

    private IReadOnlyList<TableSelectionPreset> _presets = [];

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public TableSelectionSpec ActiveSpec => _activeSpec;

    /// <inheritdoc />
    public string? ActivePresetId => _activePresetId;

    /// <inheritdoc />
    public DatabaseIdentifier? CurrentDatabase => _currentDatabase;

    /// <inheritdoc />
    public bool IsDirty => _isDirty;

    /// <inheritdoc />
    public TableSelectionSpec? OperationSpec => _activeSpec.IsActive ? _activeSpec : null;

    /// <inheritdoc />
    public IReadOnlyList<TableSelectionPreset> Presets => _presets;

    /// <summary>Initializes a new instance.</summary>
    public TableSelectionService(ITableSelectionPresetStore presetStore) =>
        _presetStore = presetStore;

    /// <inheritdoc />
    public async Task ApplyDialogSelectionAsync(string? presetId, IReadOnlySet<TableId> excludedTables)
    {
        if (_currentDatabase is null) return;

        _activePresetId = presetId;
        _activeSpec = BuildSpec(excludedTables);
        _isDirty = DiffersFromSavedPreset(presetId, excludedTables);

        await _presetStore.SetLastUsedPresetIdAsync(_currentDatabase, presetId);

        // Persist or clear the temporary exclusion set so it survives a restart.
        if (_isDirty)
            await _presetStore.SetTemporaryExclusionsAsync(_currentDatabase, excludedTables);
        else
            await _presetStore.SetTemporaryExclusionsAsync(_currentDatabase, null);

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public async Task LoadForConnectionAsync(SavedConnection? connection)
    {
        if (connection is null)
        {
            Reset();
            Changed?.Invoke();
            return;
        }

        _currentDatabase = new DatabaseIdentifier(connection.Id, connection.DatabaseName);
        _presets = await _presetStore.LoadPresetsAsync(_currentDatabase);

        var lastUsedId = await _presetStore.GetLastUsedPresetIdAsync(_currentDatabase);
        var lastUsed = _presets.FirstOrDefault(p => p.Id == lastUsedId);

        // Restore a persisted temporary (dirty) selection if one exists.
        var tempExclusions = await _presetStore.GetTemporaryExclusionsAsync(_currentDatabase);
        if (tempExclusions is not null)
        {
            _activePresetId = lastUsed?.Id;
            _activeSpec = BuildSpec(tempExclusions);
            _isDirty = true;
        }
        else
        {
            _activePresetId = lastUsed?.Id;
            _activeSpec = lastUsed is null
                ? TableSelectionSpec.All
                : BuildSpec(lastUsed.ExcludedTables);
            _isDirty = false;
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public async Task ReloadPresetsAsync()
    {
        if (_currentDatabase is null) return;

        _presets = await _presetStore.LoadPresetsAsync(_currentDatabase);
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public async Task SetActivePresetAsync(string? presetId)
    {
        if (_currentDatabase is null) return;

        _activePresetId = presetId;

        var preset = presetId is null ? null : _presets.FirstOrDefault(p => p.Id == presetId);
        _activeSpec = preset is null ? TableSelectionSpec.All : BuildSpec(preset.ExcludedTables);
        _isDirty = false;

        await _presetStore.SetLastUsedPresetIdAsync(_currentDatabase, presetId);
        await _presetStore.SetTemporaryExclusionsAsync(_currentDatabase, null);
        Changed?.Invoke();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TableSelectionSpec BuildSpec(IReadOnlySet<TableId> excludedTables) =>
        excludedTables.Count == 0
            ? TableSelectionSpec.All
            : new TableSelectionSpec(true, new HashSet<TableId>(excludedTables));

    private bool DiffersFromSavedPreset(string? presetId, IReadOnlySet<TableId> excludedTables)
    {
        if (presetId is null)
        {
            return excludedTables.Count > 0;
        }

        var saved = _presets.FirstOrDefault(p => p.Id == presetId);

        return saved is null || !saved.ExcludedTables.SetEquals(excludedTables);
    }

    private void Reset()
    {
        _currentDatabase = null;
        _presets = [];
        _activePresetId = null;
        _activeSpec = TableSelectionSpec.All;
        _isDirty = false;

        Log.Debug("[TableSelectionService] Selection reset — no source connection");
    }
}
