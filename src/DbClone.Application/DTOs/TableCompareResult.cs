namespace DbClone.Application.DTOs;

public sealed record TableCompareResult(
    bool IsMatch,
    long SourceCount,
    long DestCount,
    long RowsAdded,
    long RowsRemoved,
    long RowsModified);
