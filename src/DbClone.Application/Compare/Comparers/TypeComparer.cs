using DbClone.Application.Enums;
using DbClone.Application.Compare;
using DbClone.Application.Models;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Compares enums, domains, and composite types by presence and definition.
/// </summary>
public sealed class TypeComparer : IModelComparer
{
    public IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var items = new List<ModelCompareItem>();

        // Enums
        var srcEnums = source.Enums.ToDictionary(
            e => $"{e.SchemaName}.{e.Name}",
            e => string.Join(",", e.Labels));
        var dstEnums = dest.Enums.ToDictionary(
            e => $"{e.SchemaName}.{e.Name}",
            e => string.Join(",", e.Labels));
        items.AddRange(DictionaryCompareHelper.Compare(
            EDatabaseObjectType.Enum, srcEnums, dstEnums, ct));

        // Domains
        var srcDomains = source.Domains.ToDictionary(
            d => $"{d.SchemaName}.{d.Name}",
            d => $"{d.DataType}|{d.IsNullable}|{d.CheckExpression}|{d.DefaultValue}");
        var dstDomains = dest.Domains.ToDictionary(
            d => $"{d.SchemaName}.{d.Name}",
            d => $"{d.DataType}|{d.IsNullable}|{d.CheckExpression}|{d.DefaultValue}");
        items.AddRange(DictionaryCompareHelper.Compare(
            EDatabaseObjectType.Domain, srcDomains, dstDomains, ct));

        // Composite Types
        var srcComposites = source.CompositeTypes.ToDictionary(
            c => $"{c.SchemaName}.{c.Name}",
            c => string.Join(";", c.Attributes.Select(a => $"{a.Name}:{a.DataType}")));
        var dstComposites = dest.CompositeTypes.ToDictionary(
            c => $"{c.SchemaName}.{c.Name}",
            c => string.Join(";", c.Attributes.Select(a => $"{a.Name}:{a.DataType}")));
        items.AddRange(DictionaryCompareHelper.Compare(
            EDatabaseObjectType.CompositeType, srcComposites, dstComposites, ct));

        return items;
    }
}

