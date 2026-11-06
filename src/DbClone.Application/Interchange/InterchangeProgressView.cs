namespace DbClone.Application.Interchange;

/// <summary>
/// A throttled progress snapshot for an interchange operation.
/// The product reports per-chunk; this view is emitted at most once per 100 ms
/// or on a stage/table boundary change.
/// </summary>
/// <param name="Stage">Human-readable stage name (e.g. "writing data").</param>
/// <param name="TableOrdinal">Zero-based index of the current table within the stage.</param>
/// <param name="TableTotal">Total number of tables in the current stage.</param>
/// <param name="CurrentTable">Fully qualified table name (schema.name).</param>
/// <param name="RowsDone">Rows written or read so far; only ever increases.</param>
/// <param name="RowsTotal">Total rows expected for the current table.</param>
/// <param name="BytesDone">Bytes written or read so far; only ever increases.</param>
/// <param name="BytesTotal">Total bytes expected (estimate until the final chunk).</param>
/// <param name="Elapsed">Wall-clock time since the operation started.</param>
/// <param name="Remaining">Estimated time remaining, or <c>null</c> when not yet computable.</param>
public sealed record InterchangeProgressView(
    string Stage,
    int TableOrdinal,
    int TableTotal,
    string CurrentTable,
    long RowsDone,
    long RowsTotal,
    long BytesDone,
    long BytesTotal,
    TimeSpan Elapsed,
    TimeSpan? Remaining);
