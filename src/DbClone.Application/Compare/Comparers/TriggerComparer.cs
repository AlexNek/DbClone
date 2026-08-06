using DbClone.Application.Enums;
using DbClone.Application.Compare;
using DbClone.Application.Models;

namespace DbClone.Application.Compare.Comparers;

/// <summary>
/// Compares triggers by presence and definition (timing, events, function, level, enabled).
/// </summary>
public sealed class TriggerComparer : IModelComparer
{
    public IReadOnlyList<ModelCompareItem> Compare(
        DatabaseModel source,
        DatabaseModel dest,
        CancellationToken ct)
    {
        var srcTriggers = source.Triggers.ToDictionary(
            t => $"{t.SchemaName}.{t.TableName}.{t.Name}",
            t => $"{t.Timing}|{string.Join(",", t.Events)}|{t.FunctionSchema}.{t.FunctionName}|{t.IsRowLevel}|{t.IsEnabled}");
        var dstTriggers = dest.Triggers.ToDictionary(
            t => $"{t.SchemaName}.{t.TableName}.{t.Name}",
            t => $"{t.Timing}|{string.Join(",", t.Events)}|{t.FunctionSchema}.{t.FunctionName}|{t.IsRowLevel}|{t.IsEnabled}");

        return DictionaryCompareHelper.Compare(
            EDatabaseObjectType.Trigger, srcTriggers, dstTriggers, ct);
    }
}

