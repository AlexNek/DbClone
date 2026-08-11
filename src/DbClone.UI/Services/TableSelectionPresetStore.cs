using System.IO;
using System.Text.Json;

using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.UI.Models;

using Serilog;

namespace DbClone.UI.Services;

/// <summary>
/// File-backed store for table selection presets.
/// Persisted to %LOCALAPPDATA%/DbClone/table-selection-presets.json,
/// following the <see cref="ConnectionStore"/> conventions: one store class
/// behind an interface, indented JSON, missing file = empty defaults,
/// I/O errors logged and not thrown.
/// </summary>
public sealed class TableSelectionPresetStore : ITableSelectionPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string DefaultStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolder.Name,
        "table-selection-presets.json");

    private readonly List<DatabaseEntryDto> _databases = [];

    private readonly object _lock = new();

    private readonly string _storePath;

    /// <summary>
    /// Creates the store and loads existing presets from disk.
    /// The path can be overridden for tests.
    /// </summary>
    public TableSelectionPresetStore(string? storePath = null)
    {
        _storePath = storePath ?? DefaultStorePath;
        Load();
    }

    /// <inheritdoc />
    public Task DeletePresetAsync(DatabaseIdentifier database, string presetId)
    {
        lock (_lock)
        {
            var entry = FindOrCreateEntry(database);
            entry.Presets.RemoveAll(p => p.Id == presetId);

            if (entry.LastUsedPresetId == presetId)
                entry.LastUsedPresetId = null;
        }

        Persist();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetLastUsedPresetIdAsync(DatabaseIdentifier database)
    {
        lock (_lock)
            return Task.FromResult(FindEntry(database)?.LastUsedPresetId);
    }

    /// <inheritdoc />
    public Task<TableSelectionPreset?> GetPresetAsync(DatabaseIdentifier database, string presetId)
    {
        lock (_lock)
        {
            var dto = FindEntry(database)?.Presets.Find(p => p.Id == presetId);
            return Task.FromResult(dto?.ToModel());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TableSelectionPreset>> LoadPresetsAsync(DatabaseIdentifier database)
    {
        lock (_lock)
        {
            var entry = FindEntry(database);
            IReadOnlyList<TableSelectionPreset> presets = entry is null
                ? []
                : [.. entry.Presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(p => p.ToModel())];

            return Task.FromResult(presets);
        }
    }

    /// <inheritdoc />
    public Task RenamePresetAsync(DatabaseIdentifier database, string presetId, string newName)
    {
        lock (_lock)
        {
            var preset = FindEntry(database)?.Presets.Find(p => p.Id == presetId);

            if (preset is not null)
            {
                preset.Name = newName.Trim();
                preset.ModifiedAt = DateTime.Now;
            }
        }

        Persist();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SavePresetAsync(DatabaseIdentifier database, TableSelectionPreset preset)
    {
        lock (_lock)
        {
            var entry = FindOrCreateEntry(database);
            var existing = entry.Presets.FindIndex(p => p.Id == preset.Id);

            if (existing >= 0)
                entry.Presets[existing] = PresetDto.FromModel(preset);
            else
                entry.Presets.Add(PresetDto.FromModel(preset));
        }

        Persist();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetLastUsedPresetIdAsync(DatabaseIdentifier database, string? presetId)
    {
        lock (_lock)
            FindOrCreateEntry(database).LastUsedPresetId = presetId;

        Persist();
        return Task.CompletedTask;
    }

    // ── Lookup ───────────────────────────────────────────────────────────────────

    private DatabaseEntryDto? FindEntry(DatabaseIdentifier database) =>
        _databases.Find(e =>
            e.ConnectionProfileId == database.ConnectionProfileId
            && string.Equals(e.DatabaseName, database.DatabaseName, StringComparison.OrdinalIgnoreCase));

    private DatabaseEntryDto FindOrCreateEntry(DatabaseIdentifier database)
    {
        var entry = FindEntry(database);

        if (entry is null)
        {
            entry = new DatabaseEntryDto
            {
                ConnectionProfileId = database.ConnectionProfileId,
                DatabaseName = database.DatabaseName
            };
            _databases.Add(entry);
        }

        return entry;
    }

    // ── Serialization DTOs ───────────────────────────────────────────────────────

    /// <summary>All presets and the last-used marker for one database.</summary>
    private sealed class DatabaseEntryDto
    {
        public string ConnectionProfileId { get; set; } = string.Empty;

        public string DatabaseName { get; set; } = string.Empty;

        public string? LastUsedPresetId { get; set; }

        public List<PresetDto> Presets { get; set; } = [];
    }

    /// <summary>JSON form of a <see cref="TableSelectionPreset"/>.</summary>
    private sealed class PresetDto
    {
        public DateTime CreatedAt { get; set; }

        public List<TableIdDto> ExcludedTables { get; set; } = [];

        public string Id { get; set; } = string.Empty;

        public DateTime ModifiedAt { get; set; }

        public string Name { get; set; } = string.Empty;

        public static PresetDto FromModel(TableSelectionPreset m) =>
            new()
            {
                Id = m.Id,
                Name = m.Name,
                ExcludedTables = [.. m.ExcludedTables.Select(TableIdDto.FromModel)],
                CreatedAt = m.CreatedAt,
                ModifiedAt = m.ModifiedAt
            };

        public TableSelectionPreset ToModel() =>
            new(
                Id: Id,
                Name: Name,
                ExcludedTables: new HashSet<TableId>(ExcludedTables.Select(t => t.ToModel())),
                CreatedAt: CreatedAt,
                ModifiedAt: ModifiedAt);
    }

    /// <summary>JSON form of a <see cref="TableId"/>.</summary>
    private sealed class TableIdDto
    {
        public string Name { get; set; } = string.Empty;

        public string Schema { get; set; } = string.Empty;

        public static TableIdDto FromModel(TableId m) =>
            new() { Schema = m.Schema, Name = m.Name };

        public TableId ToModel() => new(Schema, Name);
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;

            var json = File.ReadAllText(_storePath);
            var entries = JsonSerializer.Deserialize<List<DatabaseEntryDto>>(json) ?? [];

            lock (_lock)
            {
                _databases.Clear();
                _databases.AddRange(entries);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[TableSelectionPresetStore.Load] Failed to load presets from {Path}, starting fresh",
                _storePath);
        }
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            List<DatabaseEntryDto> snapshot;
            lock (_lock)
                snapshot = [.. _databases];

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_storePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[TableSelectionPresetStore.Persist] Failed to save presets to {Path}",
                _storePath);
        }
    }
}
