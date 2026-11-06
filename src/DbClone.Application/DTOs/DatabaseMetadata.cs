namespace DbClone.Application.DTOs;

/// <summary>
/// Aggregate metadata counts for one database.
/// </summary>
public sealed record DatabaseMetadata(
    int Tables,
    int Views,
    int MaterializedViews,
    int Sequences,
    int Functions,
    int Triggers,
    int Enums,
    int Domains,
    int CompositeTypes,
    int Indexes,
    int Constraints)
{
    /// <summary>
    /// Builds a human-readable summary listing only non-zero object counts.
    /// Example: "76 tables, 3 views, 2 sequences, 56 functions, 4 enums".
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(8);
            if (Tables > 0) parts.Add($"{Tables} tables");
            if (Views > 0) parts.Add($"{Views} views");
            if (MaterializedViews > 0) parts.Add($"{MaterializedViews} materialized views");
            if (Sequences > 0) parts.Add($"{Sequences} sequences");
            if (Functions > 0) parts.Add($"{Functions} functions");
            if (Triggers > 0) parts.Add($"{Triggers} triggers");
            if (Enums > 0) parts.Add($"{Enums} enums");
            if (Domains > 0) parts.Add($"{Domains} domains");
            if (CompositeTypes > 0) parts.Add($"{CompositeTypes} composite types");
            if (Indexes > 0) parts.Add($"{Indexes} indexes");
            if (Constraints > 0) parts.Add($"{Constraints} constraints");
            return parts.Count > 0 ? string.Join(", ", parts) : "no objects";
        }
    }
}
