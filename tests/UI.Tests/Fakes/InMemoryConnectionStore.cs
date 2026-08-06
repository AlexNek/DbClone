using DbClone.UI.Models;

namespace UI.Tests.Fakes;

/// <summary>
/// In-memory implementation of <see cref="IConnectionStore"/> for testing.
/// Fires the <see cref="Changed"/> event on every mutation.
/// </summary>
public sealed class InMemoryConnectionStore : IConnectionStore
{
    public event Action? Changed;

    private readonly List<SavedConnection> _connections = [];

    public void Delete(string id)
    {
        _connections.RemoveAll(c => c.Id == id);
        Changed?.Invoke();
    }

    public IReadOnlyList<SavedConnection> GetAll() => _connections.ToList();

    public SavedConnection? GetById(string id) => _connections.FirstOrDefault(c => c.Id == id);

    public void Save(SavedConnection connection)
    {
        var existing = _connections.FindIndex(c => c.Id == connection.Id);
        if (existing >= 0)
            _connections[existing] = connection;
        else
            _connections.Add(connection);
        Changed?.Invoke();
    }
}
