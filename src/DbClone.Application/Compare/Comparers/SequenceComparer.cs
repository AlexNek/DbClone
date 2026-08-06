using DbClone.Application.Enums;
using DbClone.Application.Compare;
using DbClone.Application.Models;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Compares standalone and serial sequences by presence and configuration.
/// Identity backing sequences (deptype 'i') are excluded: their names are non-deterministic
/// across databases and they are implicitly created by the owning table's DDL.
/// Serial sequences (deptype 'a') have deterministic names (referenced in column defaults)
/// and are compared normally.
/// </summary>
public sealed class SequenceComparer : IModelComparer
{
    public IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var srcSeqs = source.Sequences
            .Where(s => !s.IsIdentity)
            .ToDictionary(
                s => $"{s.SchemaName}.{s.Name}",
                s => $"{s.DataType}|{s.IncrementBy}|{s.MinValue}|{s.MaxValue}|{s.IsCycled}");
        var dstSeqs = dest.Sequences
            .Where(s => !s.IsIdentity)
            .ToDictionary(
                s => $"{s.SchemaName}.{s.Name}",
                s => $"{s.DataType}|{s.IncrementBy}|{s.MinValue}|{s.MaxValue}|{s.IsCycled}");

        return DictionaryCompareHelper.Compare(
            EDatabaseObjectType.Sequence, srcSeqs, dstSeqs, ct);
    }
}

