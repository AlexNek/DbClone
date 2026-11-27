namespace DbClone.Application.Interchange;

/// <summary>
/// Progress information for blob extraction.
/// </summary>
/// <param name="Stage">Current stage description.</param>
/// <param name="RowsProcessed">Number of rows processed so far.</param>
/// <param name="TotalRows">Total number of rows to process.</param>
public sealed record BlobExtractionProgressView(
    string Stage,
    int RowsProcessed,
    int TotalRows);
