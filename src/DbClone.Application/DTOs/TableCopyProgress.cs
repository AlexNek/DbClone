namespace DbClone.Application.DTOs;

/// <summary>
/// Progress information for table copy.
/// </summary>
public sealed record TableCopyProgress(
    string SchemaName,
    string TableName,
    long RowsCopied,
    long TotalRows,
    TimeSpan Elapsed);
