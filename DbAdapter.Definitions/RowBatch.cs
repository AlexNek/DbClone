using CanonicalModel.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// A batch of rows for interchange operations.
/// </summary>
public sealed class RowBatch : IAsyncDisposable
{
    private readonly IAsyncEnumerator<DataValue[]>? _enumerator;
    private bool _disposed;

    /// <summary>
    /// Creates a new row batch from an async enumerable.
    /// </summary>
    /// <param name="rows">The rows to enumerate.</param>
    public RowBatch(IAsyncEnumerable<DataValue[]> rows)
    {
        _enumerator = rows.GetAsyncEnumerator();
    }

    /// <summary>
    /// Creates a new row batch from a pre-materialized list.
    /// </summary>
    /// <param name="rows">The rows.</param>
    public RowBatch(IReadOnlyList<DataValue[]> rows)
    {
        _enumerator = EnumerateAsync(rows).GetAsyncEnumerator();
    }

    /// <summary>Gets the rows in this batch.</summary>
    public IReadOnlyList<DataValue[]> Rows { get; private set; } = [];

    /// <summary>Gets whether more rows are available.</summary>
    public bool HasMore => !_disposed;

    /// <summary>
    /// Reads the next batch of rows.
    /// </summary>
    /// <param name="maxRows">Maximum number of rows to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows read.</returns>
    public async Task<IReadOnlyList<DataValue[]>> ReadNextBatchAsync(int maxRows, CancellationToken ct = default)
    {
        if (_enumerator is null || _disposed)
        {
            return [];
        }

        var batch = new List<DataValue[]>(maxRows);
        for (var i = 0; i < maxRows; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _enumerator.MoveNextAsync())
            {
                _disposed = true;
                break;
            }
            batch.Add(_enumerator.Current);
        }

        Rows = batch;
        return batch;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_enumerator is not null)
        {
            await _enumerator.DisposeAsync();
        }
        _disposed = true;
    }

    private static async IAsyncEnumerable<DataValue[]> EnumerateAsync(IReadOnlyList<DataValue[]> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }
        await Task.CompletedTask;
    }
}

/// <summary>
/// A single data value in the interchange model.
/// </summary>
public sealed record DataValue
{
    /// <summary>Gets the logical type.</summary>
    public ELogicalType LogicalType { get; init; }

    /// <summary>Gets the text representation.</summary>
    public string? Value { get; init; }

    /// <summary>Gets whether this value is null.</summary>
    public bool IsNull { get; init; }

    /// <summary>Creates a null value.</summary>
    public static DataValue Null(ELogicalType type) => new() { LogicalType = type, IsNull = true };

    /// <summary>Creates a text value.</summary>
    public static DataValue FromText(ELogicalType type, string text) => new() { LogicalType = type, Value = text, IsNull = false };
}
