namespace DbClone.Application.DTOs;

/// <summary>
/// Statistics about the copy operation.
/// </summary>
public sealed record CopyStatistics(
    long TotalRowsCopied,
    long TotalBytesTransferred,
    int TablesCopied,
    int ViewsCopied,
    int FunctionsCopied,
    int TriggersCopied,
    int IndexesCreated,
    int ConstraintsCreated,
    int SequencesSynced,
    int TablesFailed = 0);
