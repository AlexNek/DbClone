namespace DbClone.UI.Models;

public sealed record ComparisonReportData(
    IReadOnlyList<CompareResultItem> Items,
    string SourceSummary,
    string DestSummary,
    int TotalIdentical,
    int TotalNotices,
    int TotalDifferent,
    int TotalMissingSource,
    int TotalMissingDest,
    int TotalSkipped,
    int TotalErrors,
    string Duration);
