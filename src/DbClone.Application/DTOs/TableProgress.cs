namespace DbClone.Application.DTOs;

/// <summary>
/// Per-table progress during the CopyData stage.
/// </summary>
public sealed record TableProgress(
    string TableName,
    long RowsCompleted,
    long TotalRows,
    double ElapsedSeconds);
