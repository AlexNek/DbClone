using System.IO;
using System.Text.Json;

using DbClone.UI.Models;

using Serilog;

namespace DbClone.UI.Services;

/// <summary>
/// File-backed store for connection groups.
/// Persisted to %LOCALAPPDATA%/DbClone/connection-groups.json.
/// </summary>
public sealed class ConnectionGroupStore : IConnectionGroupStore
{
    /// <inheritdoc />
    public event Action? Changed;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolder.Name,
        "connection-groups.json");

    private readonly List<ConnectionGroup> _groups = [];

    private readonly object _lock = new();

    /// <summary>Creates the store and loads existing groups from disk.</summary>
    public ConnectionGroupStore()
    {
        Load();
    }

    /// <inheritdoc />
    public void Delete(string id)
    {
        lock (_lock)
            _groups.RemoveAll(g => g.Id == id);

        Persist();
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public IReadOnlyList<ConnectionGroup> GetAll()
    {
        lock (_lock)
            return _groups.ToList();
    }

    /// <inheritdoc />
    public ConnectionGroup? GetById(string id)
    {
        lock (_lock)
            return _groups.Find(g => g.Id == id);
    }

    /// <inheritdoc />
    public void Save(ConnectionGroup group)
    {
        lock (_lock)
        {
            var existing = _groups.FindIndex(g => g.Id == group.Id);
            if (existing >= 0)
                _groups[existing] = group;
            else
                _groups.Add(group);
        }

        Persist();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;

            var json = File.ReadAllText(StorePath);
            var groups = JsonSerializer.Deserialize<List<ConnectionGroup>>(json) ?? [];

            lock (_lock)
            {
                _groups.Clear();
                _groups.AddRange(groups);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[ConnectionGroupStore.Load] Failed to load groups from {Path}",
                StorePath);
        }
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            List<ConnectionGroup> snapshot;
            lock (_lock)
                snapshot = [.. _groups];

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(StorePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[ConnectionGroupStore.Persist] Failed to save groups to {Path}",
                StorePath);
        }
    }
}
