using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using DbClone.UI.Models;

using Serilog;

namespace DbClone.UI.Services;

/// <summary>
/// File-backed connection store that encrypts passwords with Windows DPAPI.
/// Persisted to %LOCALAPPDATA%/DbClone/connections.json.
/// </summary>
public sealed class ConnectionStore : IConnectionStore
{
    /// <inheritdoc />
    public event Action? Changed;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolder.Name,
        "connections.json");

    private readonly List<SavedConnection> _connections = [];

    private readonly object _lock = new();

    /// <summary>
    /// Creates the store and loads existing connections from disk.
    /// </summary>
    public ConnectionStore()
    {
        Load();
    }

    /// <inheritdoc />
    public void Delete(string id)
    {
        lock (_lock)
            _connections.RemoveAll(c => c.Id == id);

        Persist();
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public IReadOnlyList<SavedConnection> GetAll()
    {
        lock (_lock)
            return _connections.ToList(); // defensive copy
    }

    /// <inheritdoc />
    public SavedConnection? GetById(string id)
    {
        lock (_lock)
            return _connections.Find(c => c.Id == id);
    }

    /// <inheritdoc />
    public void Save(SavedConnection connection)
    {
        lock (_lock)
        {
            var existing = _connections.FindIndex(c => c.Id == connection.Id);
            if (existing >= 0)
                _connections[existing] = connection;
            else
                _connections.Add(connection);
        }

        Persist();
        Changed?.Invoke();
    }

    // ── Serialization DTO ────────────────────────────────────────────────────────

    /// <summary>
    /// DTO for JSON persistence. Password is stored as a DPAPI-encrypted Base64 string.
    /// </summary>
    private sealed class ConnectionDto
    {
        public string BackupName { get; set; } = string.Empty;

        public string? Color { get; set; }

        [JsonConverter(typeof(ConnectionTypeConverter))]
        public string? ConnectionType { get; set; }

        public string DatabaseName { get; set; } = string.Empty;

        /// <summary>DPAPI-encrypted password (Base64).</summary>
        public string EncryptedPassword { get; set; } = string.Empty;

        public string Folder { get; set; } = "Local";

        public string Host { get; set; } = "localhost";

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;

        public string Port { get; set; } = "5432";

        public string SslMode { get; set; } = "Prefer";

        public string Username { get; set; } = "postgres";

        public static ConnectionDto FromModel(SavedConnection m) =>
            new()
                {
                    Id = m.Id,
                    Name = m.Name,
                    Host = m.Host,
                    Port = m.Port,
                    DatabaseName = m.DatabaseName,
                    Username = m.Username,
                    EncryptedPassword = Encrypt(m.Password),
                    SslMode = m.SslMode,
                    ConnectionType = m.ConnectionType,
                    Notes = m.Notes,
                    BackupName = m.BackupName,
                    Folder = m.Folder,
                    Color = m.Color
                };

        public SavedConnection ToModel() =>
            new()
                {
                    Id = Id,
                    Name = Name,
                    Host = Host,
                    Port = Port,
                    DatabaseName = DatabaseName,
                    Username = Username,
                    Password = Decrypt(EncryptedPassword),
                    SslMode = SslMode,
                    ConnectionType = ConnectionType,
                    Notes = Notes,
                    BackupName = BackupName,
                    Folder = Folder,
                    Color = Color
                };
    }

    private static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;
        try
        {
            var cipher = Convert.FromBase64String(cipherText);
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Decryption failure (e.g. data was encrypted on another machine) — return empty
            return string.Empty;
        }
    }

    // ── DPAPI helpers ────────────────────────────────────────────────────────────

    private static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var plain = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;

            var json = File.ReadAllText(StorePath);
            var dtos = JsonSerializer.Deserialize<List<ConnectionDto>>(json) ?? [];

            lock (_lock)
            {
                _connections.Clear();
                foreach (var dto in dtos)
                    _connections.Add(dto.ToModel());
            }
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[ConnectionStore.Load] Failed to load connections from {Path}, starting fresh",
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

            List<ConnectionDto> dtos;
            lock (_lock)
                dtos = _connections.Select(ConnectionDto.FromModel).ToList();

            var json = JsonSerializer.Serialize(dtos, JsonOptions);
            File.WriteAllText(StorePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[ConnectionStore.Persist] Failed to save connections to {Path}",
                StorePath);
        }
    }
}
