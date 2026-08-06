using DbClone.Application.DTOs;
using DbClone.Application.Enums;

namespace DbClone.PostgreSql.DependencyAnalysis;

/// <summary>
/// Orders work items according to the topological order computed by
/// <see cref="PgDependencyAnalyzer"/>. Items whose key is not present in the
/// analysis result keep their original relative order and are placed after
/// all ordered items, so this is always safe to apply.
/// </summary>
internal static class DependencyOrdering
{
    /// <summary>
    /// Stable-sorts <paramref name="items"/> by their position in
    /// <paramref name="result"/>.OrderedObjects.
    /// </summary>
    /// <param name="items">The items to sort.</param>
    /// <param name="result">The dependency analysis result providing the topological order, or null to return items unchanged.</param>
    /// <param name="keySelector">
    /// Maps an item to its object type and "schema.name" qualified name,
    /// matching the keys produced by the analyzer.
    /// </param>
    public static IReadOnlyList<T> Sort<T>(
        IEnumerable<T> items,
        DependencyResult? result,
        Func<T, (EDatabaseObjectType Type, string QualifiedName)> keySelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);

        var list = items.ToList();
        if (result is null || result.OrderedObjects.Count == 0)
            return list;

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < result.OrderedObjects.Count; i++)
        {
            var o = result.OrderedObjects[i];
            index.TryAdd($"{o.ObjectType}:{o.SchemaName}.{o.Name}", i);
        }

        return list
            .Select((item, original) => (item, original))
            .OrderBy(x =>
                {
                    var (type, qualifiedName) = keySelector(x.item);
                    return index.GetValueOrDefault($"{type}:{qualifiedName}", int.MaxValue);
                })
            .ThenBy(x => x.original)
            .Select(x => x.item)
            .ToList();
    }
}
