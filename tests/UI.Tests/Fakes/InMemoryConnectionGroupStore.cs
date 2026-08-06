using DbClone.UI.Models;

namespace UI.Tests.Fakes;

/// <summary>
/// In-memory implementation of <see cref="IConnectionGroupStore"/> for testing.
/// Fires the <see cref="Changed"/> event on every mutation.
/// </summary>
public sealed class InMemoryConnectionGroupStore : IConnectionGroupStore
{
    public event Action? Changed;

    private readonly List<ConnectionGroup> _groups = [];

    public void Delete(string id)
    {
        _groups.RemoveAll(g => g.Id == id);
        Changed?.Invoke();
    }

    public IReadOnlyList<ConnectionGroup> GetAll() => _groups.ToList();

    public ConnectionGroup? GetById(string id) => _groups.FirstOrDefault(g => g.Id == id);

    public void Save(ConnectionGroup group)
    {
        var existing = _groups.FindIndex(g => g.Id == group.Id);
        if (existing >= 0)
            _groups[existing] = group;
        else
            _groups.Add(group);
        Changed?.Invoke();
    }
}
