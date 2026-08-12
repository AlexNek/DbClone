namespace DbClone.Application.Models;

/// <summary>
/// Estimated on-disk size of a table including indexes and TOAST data.
/// Provider-reported estimate used for display only — never drives selection logic.
/// </summary>
public sealed record TableSizeInfo(TableId Table, long SizeBytes);
