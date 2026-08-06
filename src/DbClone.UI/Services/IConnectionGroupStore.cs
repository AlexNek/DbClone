using DbClone.UI.Models;

namespace DbClone.UI.Services;

/// <summary>
/// Persistent store for connection groups (source + destination pairs).
/// </summary>
public interface IConnectionGroupStore
{
    /// <summary>Raised when the collection changes so UI can refresh.</summary>
    event Action? Changed;

    /// <summary>Removes a connection group by its Id.</summary>
    void Delete(string id);

    /// <summary>Returns all saved connection groups.</summary>
    IReadOnlyList<ConnectionGroup> GetAll();

    /// <summary>Returns a single group by its Id, or null if not found.</summary>
    ConnectionGroup? GetById(string id);

    /// <summary>Adds or updates a connection group.</summary>
    void Save(ConnectionGroup group);
}
