using DbClone.Application.Enums;

namespace DbClone.UI.Models;

public sealed class CompareResultItem
{
    public long DestCount { get; set; }

    /// <summary>Display-friendly dest count; shows "N/A" when count is unavailable (-1).</summary>
    public string DestCountDisplay => DestCount < 0 ? "N/A" : DestCount.ToString("N0");

    /// <summary>
    /// Why this item was skipped. Null when Status is not <see cref="ECompareStatus.Skipped"/>.
    /// </summary>
    public ESkipReason? SkipReason { get; set; }

    /// <summary>
    /// Which side caused the skip (e.g. which connection lacked permissions).
    /// Null when not applicable.
    /// </summary>
    public ECompareSide? SkipSide { get; set; }

    public string Details { get; set; } = "";

    /// <summary>Object category.</summary>
    public EDatabaseObjectType ObjectType { get; set; } = EDatabaseObjectType.Table;

    /// <summary>Human-friendly display name for the object type.</summary>
    public string ObjectTypeDisplay =>
        ObjectType switch
            {
                EDatabaseObjectType.Schema => "Schema",
                EDatabaseObjectType.Table => "Table",
                EDatabaseObjectType.Index => "Index",
                EDatabaseObjectType.View => "View",
                EDatabaseObjectType.MaterializedView => "Materialized View",
                EDatabaseObjectType.Function => "Function",
                EDatabaseObjectType.Sequence => "Sequence",
                EDatabaseObjectType.Trigger => "Trigger",
                EDatabaseObjectType.Enum => "Enum",
                EDatabaseObjectType.Domain => "Domain",
                EDatabaseObjectType.CompositeType => "Composite Type",
                _ => ObjectType.ToString()
            };

    public long RowDiff => SourceCount - DestCount;

    public long RowsAdded { get; set; }

    public long RowsModified { get; set; }

    public long RowsRemoved { get; set; }

    /// <summary>Schema the object belongs to (e.g. "public").</summary>
    public string SchemaName { get; set; } = "";

    public long SourceCount { get; set; }

    /// <summary>Display-friendly source count; shows "N/A" when count is unavailable (-1).</summary>
    public string SourceCountDisplay => SourceCount < 0 ? "N/A" : SourceCount.ToString("N0");

    public ECompareStatus Status { get; set; }

    public string StatusText =>
        Status switch
            {
                ECompareStatus.Identical => nameof(ECompareStatus.Identical),
                ECompareStatus.Notice => "Notice",
                ECompareStatus.Different => nameof(ECompareStatus.Different),
                ECompareStatus.MissingSource => "Missing in Source",
                ECompareStatus.MissingDest => "Missing in Dest",
                ECompareStatus.Skipped => "Skipped",
                ECompareStatus.Error => "Error",
                _ => "Unknown"
            };

    public string TableName { get; set; } = "";
}
