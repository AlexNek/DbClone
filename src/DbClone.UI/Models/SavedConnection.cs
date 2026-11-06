using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DbClone.UI.Models;

/// <summary>
/// Represents a saved database connection with all configuration needed to reconnect.
/// </summary>
public sealed class SavedConnection : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _color;
    private bool _isItemSelected;

    /// <summary>Optional short label used as backup database name prefix (e.g. "crm", "analytics").</summary>
    public string BackupName { get; set; } = string.Empty;

    /// <summary>Optional hex colour tag (e.g. "#4CAF50") for visual identification.</summary>
    public string? Color
    {
        get => _color;
        set
        {
            if (_color != value)
            {
                _color = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>UI-only flag indicating this item is currently selected in the list.</summary>
    [JsonIgnore]
    public bool IsItemSelected
    {
        get => _isItemSelected;
        set
        {
            if (_isItemSelected != value)
            {
                _isItemSelected = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Provider type hint — the .platform stable id (e.g. "postgresql", "supabase"). Null = base engine.</summary>
    [JsonConverter(typeof(ConnectionTypeConverter))]
    public string? ConnectionType { get; set; }

    /// <summary>
    /// The engine dialect that selects this connection's adapter (refactors/09 P4). Written when a
    /// connection is created or imported from the import/detection result, never from a constant.
    /// Absent (<see langword="null"/>) on files saved before the engine was recorded; the single
    /// installed adapter then serves them.
    /// </summary>
    public string? Engine { get; set; }

    /// <summary>Database name to connect to.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Folder/group name for organising connections in the tree view.</summary>
    public string Folder { get; set; } = "Local";

    /// <summary>Server hostname or IP address.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Unique identifier for this connection entry.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name shown in dropdowns and the connection manager.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-text notes for the user's reference.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Password — stored DPAPI-encrypted on disk; plain text only in memory.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Port number as a string for easy WPF binding. Empty until set; a new connection's
    /// port comes from the platform defaults, not from an engine value stored here (refactors/09 P5).</summary>
    public string Port { get; set; } = string.Empty;

    /// <summary>Gets the parsed port as an integer, or 0 when no port is set.</summary>
    [JsonIgnore]
    public int PortNumber => int.TryParse(Port, out var p) ? p : 0;

    /// <summary>SSL mode carried as the engine's TLS identifier; the adapter converts at the boundary.</summary>
    public string SslMode { get; set; } = string.Empty;

    /// <summary>Gets a summary string for display: host:port/database.</summary>
    [JsonIgnore]
    public string Summary =>
        string.IsNullOrEmpty(DatabaseName)
            ? $"{Host}:{Port}"
            : $"{Host}:{Port}/{DatabaseName}";

    /// <summary>Login username. Empty until set; a new connection's username comes from the platform defaults.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Creates a deep copy of this connection.</summary>
    public SavedConnection Clone() =>
        new()
            {
                Id = Id,
                Name = Name,
                Host = Host,
                Port = Port,
                DatabaseName = DatabaseName,
                Username = Username,
                Password = Password,
                SslMode = SslMode,
                ConnectionType = ConnectionType,
                Engine = Engine,
                Notes = Notes,
                BackupName = BackupName,
                Folder = Folder,
                Color = Color
            };

    /// <summary>Returns the display name (used by ComboBox default rendering).</summary>
    public override string ToString() => Name;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
