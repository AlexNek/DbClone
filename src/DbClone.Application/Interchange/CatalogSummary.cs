namespace DbClone.Application.Interchange;

/// <summary>
/// Offline summary of a catalog's contents, read from the manifest and <c>export.json</c>
/// without opening a database connection.
/// </summary>
public sealed record CatalogSummary(
    int FormatVersion,
    string Producer,
    DateTimeOffset? ExportTime,
    bool IsComplete,
    int Sections,
    int Tables,
    long Rows,
    int Chunks,
    long Bytes,
    IReadOnlyDictionary<string, int> PreservationCounts,
    IReadOnlyDictionary<string, int> SecurityCounts,
    IReadOnlyList<InterchangeIssueView> ProducerIssues);
