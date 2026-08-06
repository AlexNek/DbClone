using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DbClone.UI.Models;

/// <summary>
/// A named pair of source and destination connections that can be loaded together.
/// </summary>
public sealed class ConnectionGroup : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _color;

    /// <summary>Optional hex colour tag for visual identification.</summary>
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

    /// <summary>Id of the destination <see cref="SavedConnection"/>.</summary>
    public string DestinationConnectionId { get; set; } = string.Empty;

    /// <summary>Unique identifier for this group.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name for the group (e.g. "Dev → Staging").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description or notes.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Id of the source <see cref="SavedConnection"/>.</summary>
    public string SourceConnectionId { get; set; } = string.Empty;

    /// <summary>Returns the display name.</summary>
    public override string ToString() => Name;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
