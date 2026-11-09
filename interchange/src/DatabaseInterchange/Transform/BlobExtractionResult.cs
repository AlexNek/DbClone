namespace DatabaseInterchange.Transform;

/// <summary>
/// Result of a blob extraction operation.
/// </summary>
/// <param name="FilesExtracted">Number of files extracted.</param>
/// <param name="BytesExtracted">Total bytes extracted.</param>
/// <param name="RowsProcessed">Number of rows processed.</param>
/// <param name="Elapsed">Time taken for the extraction.</param>
public sealed record BlobExtractionResult(
    int FilesExtracted,
    long BytesExtracted,
    int RowsProcessed,
    TimeSpan Elapsed);
