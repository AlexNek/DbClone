using DbClone.Application.Enums;
using DbClone.Application.Compare;
using DbClone.Application.Models;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Compares views and materialized views by presence and normalized definition.
/// </summary>
public sealed class ViewComparer : IModelComparer
{
    public IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var items = new List<ModelCompareItem>();

        // Views
        var srcViews = source.Views.ToDictionary(
            v => $"{v.SchemaName}.{v.Name}",
            v => NormalizeDef(v.Definition));
        var dstViews = dest.Views.ToDictionary(
            v => $"{v.SchemaName}.{v.Name}",
            v => NormalizeDef(v.Definition));
        items.AddRange(DictionaryCompareHelper.Compare(
            EDatabaseObjectType.View, srcViews, dstViews, ct));

        // Materialized Views
        var srcMatViews = source.MaterializedViews.ToDictionary(
            v => $"{v.SchemaName}.{v.Name}",
            v => NormalizeDef(v.Definition));
        var dstMatViews = dest.MaterializedViews.ToDictionary(
            v => $"{v.SchemaName}.{v.Name}",
            v => NormalizeDef(v.Definition));
        items.AddRange(DictionaryCompareHelper.Compare(
            EDatabaseObjectType.MaterializedView, srcMatViews, dstMatViews, ct));

        return items;
    }

    private static string NormalizeDef(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return "";
        return string.Join(
            " ",
            definition.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }
}

