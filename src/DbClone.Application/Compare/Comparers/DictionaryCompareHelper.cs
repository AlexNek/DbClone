using DbClone.Application.Enums;
using DbClone.Application.Compare;
using DbClone.Application.Models;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Shared helper for dictionary-based presence/definition comparison.
/// Used by all model comparers that follow the pattern: build key → definition dictionaries,
/// then diff them.
/// </summary>
public static class DictionaryCompareHelper
{
    /// <summary>
    /// Compares two dictionaries (key → definition string) and produces comparison items.
    /// Items present only in source, only in dest, or in both (identical vs different).
    /// </summary>
    public static IReadOnlyList<ModelCompareItem> Compare(
        EDatabaseObjectType objectType,
        Dictionary<string, string> sourceDict,
        Dictionary<string, string> destDict,
        CancellationToken ct)
    {
        var items = new List<ModelCompareItem>();

        foreach (var (key, def) in sourceDict)
        {
            ct.ThrowIfCancellationRequested();
            var schema = ExtractSchema(key);

            if (!destDict.TryGetValue(key, out var destDef))
            {
                items.Add(new ModelCompareItem(
                    objectType, schema, key,
                    ECompareStatus.MissingDest,
                    "Exists only in source"));
            }
            else
            {
                var match = string.Equals(def, destDef, StringComparison.Ordinal);
                items.Add(new ModelCompareItem(
                    objectType, schema, key,
                    match ? ECompareStatus.Identical : ECompareStatus.Different,
                    match ? "" : "Definition differs"));
            }
        }

        foreach (var key in destDict.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!sourceDict.ContainsKey(key))
            {
                items.Add(new ModelCompareItem(
                    objectType, ExtractSchema(key), key,
                    ECompareStatus.MissingSource,
                    "Exists only in destination"));
            }
        }

        return items;
    }

    private static string ExtractSchema(string key)
    {
        var dotIdx = key.IndexOf('.');
        return dotIdx >= 0 ? key[..dotIdx] : "";
    }
}

