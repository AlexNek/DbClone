using DbClone.Application.Enums;
using DbAdapter.Definitions;
using CanonicalModel.Models;
using CanonicalModel.Enums;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Compares non-primary indexes by presence and definition.
/// </summary>
public sealed class IndexComparer : IModelComparer
{
    public IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var srcIndexes = source.Tables
            .SelectMany(t => t.Indexes.Where(i => !i.IsPrimary).Select(i =>
                (Key: $"{t.SchemaName}.{t.Name}.{i.Name}", Def: GetSignature(i))))
            .ToDictionary(x => x.Key, x => x.Def);
        var dstIndexes = dest.Tables
            .SelectMany(t => t.Indexes.Where(i => !i.IsPrimary).Select(i =>
                (Key: $"{t.SchemaName}.{t.Name}.{i.Name}", Def: GetSignature(i))))
            .ToDictionary(x => x.Key, x => x.Def);

        return DictionaryCompareHelper.Compare(
            EDatabaseObjectType.Index, srcIndexes, dstIndexes, ct);
    }

    private static string GetSignature(IndexDefinition idx) =>
        idx.Definition
        ?? $"{string.Join(",", idx.Columns)}|{idx.IsUnique}|{idx.FilterExpression ?? ""}";
}

