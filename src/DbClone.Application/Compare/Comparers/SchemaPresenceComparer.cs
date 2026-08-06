using DbClone.Application.Enums;
using DbClone.Application.Compare;
using DbClone.Application.Models;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Compares schemas by presence. A schema present on both sides is Identical;
/// an owner mismatch is reported as Notice (one level below Different) because
/// roles legitimately differ between hosting platforms.
/// System schemas (pg_catalog, information_schema, …) are compared by presence
/// only: their contents are never read, but a missing system schema (e.g. a
/// dropped information_schema) must be surfaced.
/// </summary>
public sealed class SchemaPresenceComparer : IModelComparer
{
    public IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var srcSchemas = source.Schemas.Where(s => !s.IsSystem).ToDictionary(
            s => s.Name,
            s => s.Owner,
            StringComparer.OrdinalIgnoreCase);
        var dstSchemas = dest.Schemas.Where(s => !s.IsSystem).ToDictionary(
            s => s.Name,
            s => s.Owner,
            StringComparer.OrdinalIgnoreCase);

        var items = new List<ModelCompareItem>();

        foreach (var (name, srcOwner) in srcSchemas)
        {
            ct.ThrowIfCancellationRequested();

            if (!dstSchemas.TryGetValue(name, out var dstOwner))
            {
                items.Add(new ModelCompareItem(
                    EDatabaseObjectType.Schema, name, name,
                    ECompareStatus.MissingDest,
                    "Exists only in source"));
                continue;
            }

            // Presence matches. Owner is non-structural (it reflects the
            // connecting/platform role), so a mismatch is a Notice, not Different.
            var ownerMatch = string.Equals(srcOwner, dstOwner, StringComparison.Ordinal);
            items.Add(new ModelCompareItem(
                EDatabaseObjectType.Schema, name, name,
                ownerMatch ? ECompareStatus.Identical : ECompareStatus.Notice,
                ownerMatch
                    ? ""
                    : $"Owner differs (source: {srcOwner}, dest: {dstOwner})"));
        }

        foreach (var name in dstSchemas.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!srcSchemas.ContainsKey(name))
            {
                items.Add(new ModelCompareItem(
                    EDatabaseObjectType.Schema, name, name,
                    ECompareStatus.MissingSource,
                    "Exists only in destination"));
            }
        }

        items.AddRange(CompareSystemSchemaPresence(source, dest, ct));
        return items;
    }

    private static IEnumerable<ModelCompareItem> CompareSystemSchemaPresence(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var srcSystem = source.Schemas.Where(s => s.IsSystem)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dstSystem = dest.Schemas.Where(s => s.IsSystem)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in srcSystem.Union(dstSystem, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var inSource = srcSystem.Contains(name);
            var inDest = dstSystem.Contains(name);

            yield return new ModelCompareItem(
                EDatabaseObjectType.Schema,
                name,
                name,
                (inSource, inDest) switch
                    {
                        (true, true) => ECompareStatus.Identical,
                        (true, false) => ECompareStatus.MissingDest,
                        _ => ECompareStatus.MissingSource
                    },
                (inSource, inDest) switch
                    {
                        (true, true) =>
                            "System schema present in both (contents not compared)",
                        (true, false) => "System schema missing in destination",
                        _ => "System schema missing in source"
                    });
        }
    }
}

