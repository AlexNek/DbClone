using DbClone.UI.Models;

namespace DbClone.UI.Services;

/// <summary>
/// Persistent store for saved database connections.
/// </summary>
public interface IConnectionStore
{
    /// <summary>Raised when the collection changes so UI can refresh dropdowns.</summary>
    event Action? Changed;

    /// <summary>Removes a connection by its Id.</summary>
    void Delete(string id);

    /// <summary>Returns all saved connections (passwords decrypted in memory).</summary>
    IReadOnlyList<SavedConnection> GetAll();

    /// <summary>Returns a single connection by its Id, or null if not found.</summary>
    SavedConnection? GetById(string id);

    /// <summary>Adds or updates a connection. Password is encrypted before persisting.</summary>
    void Save(SavedConnection connection);
}
