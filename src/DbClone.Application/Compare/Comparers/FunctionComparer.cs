using DbClone.Application.Enums;
using DbClone.Application.Compare;
using DbClone.Application.Models;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Compares functions and procedures by presence and normalized definition.
/// </summary>
public sealed class FunctionComparer : IModelComparer
{
    public IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var srcFuncs = source.Functions
            .GroupBy(f =>
                $"{f.SchemaName}.{f.Name}({string.Join(",", f.Parameters.Select(p => p.DataType))})")
            .ToDictionary(g => g.Key, g => NormalizeDef(g.First().Definition));
        var dstFuncs = dest.Functions
            .GroupBy(f =>
                $"{f.SchemaName}.{f.Name}({string.Join(",", f.Parameters.Select(p => p.DataType))})")
            .ToDictionary(g => g.Key, g => NormalizeDef(g.First().Definition));

        return DictionaryCompareHelper.Compare(
            EDatabaseObjectType.Function, srcFuncs, dstFuncs, ct);
    }

    private static string NormalizeDef(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return "";
        return string.Join(
            " ",
            definition.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }
}

