namespace DbClone.Application.DTOs;

/// <summary>
/// Aggregate result of a database comparison.
/// </summary>
public sealed record CompareDatabasesResult(
    List<CompareResultItem> Items,
    int TotalIdentical,
    int TotalNotices,
    int TotalDifferent,
    int TotalMissingSource,
    int TotalMissingDest,
    int TotalSkipped,
    int TotalErrors);
