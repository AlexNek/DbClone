using DbClone.Application.Models;

namespace DbClone.Application.Compare;

/// <summary>
/// Compares a specific aspect of two database models and produces comparison result items.
/// Each implementation is responsible for one category of database objects (SRP).
/// New comparison dimensions can be added without modifying existing code (OCP).
/// </summary>
public interface IModelComparer
{
    /// <summary>
    /// Compares the relevant objects between source and destination models.
    /// </summary>
    IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct);
}
