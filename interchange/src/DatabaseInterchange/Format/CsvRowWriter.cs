using System.Security.Cryptography;
using System.Text;
using CanonicalModel.Interchange;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Format;

/// <summary>
/// Streams rows to chunk files with write-through semantics: each row is serialized once,
/// written immediately, and fed into an <see cref="IncrementalHash"/>. No chunk accumulation
/// in memory — the writer holds at most one serialized row at a time.
/// </summary>
/// <remarks>
/// <para>Chunk boundaries are decided after each row write: when the accumulated byte count
/// reaches <see cref="ExportOptions.ChunkBytes"/> or the row count reaches
/// <see cref="ExportOptions.MaxRowsPerChunk"/>, the current chunk is sealed and a new one
/// begins. A row that pushes the byte count past the limit stays in the current chunk (the
/// limit is approximate, not a hard ceiling on file size). The row count is additionally capped
/// at <see cref="ExcelRowCeiling"/> so every chunk file opens in Excel (part A/R3).</para>
/// <para>Every non-NULL Binary value is written to its own out-of-band file via
/// <see cref="BlobContainerWriter"/>; the CSV row keeps only a reference.</para>
/// </remarks>
internal sealed class CsvRowWriter : IAsyncDisposable
{
    /// <summary>
    /// The hard row cap per chunk file, below Excel's 1 048 576 rows per sheet, so every chunk a
    /// catalog produces can be opened in Excel (part A/R3). It binds whatever the user sets, because
    /// it is a property of the reader's file format and not a preference.
    /// </summary>
    internal const int ExcelRowCeiling = 1_000_000;

    /// <summary>
    /// The bytes the chunk's encoder writes before its first row. They are part of the stored file, so the
    /// hash the write pass records has to cover them (part A/R8) — <c>checksums.sha256</c> is the list a
    /// restore treats as authoritative, and a hash that answers only part of its file is a false claim.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Utf8Preamble =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();

    private readonly string _tableDir;
    private readonly int _maxChunkBytes;
    private readonly int _maxRowsPerChunk;
    private readonly IReadOnlyList<string> _columnNames;
    private readonly IReadOnlyList<string> _columnSegments;
    private readonly List<TableChunkInfo> _chunks = [];
    private readonly List<BlobContainerInfo> _allBlobContainers = [];
    // Per-column, per-suffix count of values written to containers
    private readonly Dictionary<string, Dictionary<string, int>> _blobFormatCounts = new(StringComparer.Ordinal);

    private StreamWriter? _writer;
    private IncrementalHash? _hash;
    private BlobContainerWriter? _blobWriter;
    private int _chunkIndex;
    private int _rowsInChunk;
    private long _bytesInChunk;
    private long _blobBytesInChunk;
    private bool _chunkStarted;
    private bool _disposed;

    /// <summary>
    /// Initializes a new streaming row writer for the specified table directory.
    /// </summary>
    /// <param name="tableDir">The table's data directory (e.g. <c>data/public/orders</c>).</param>
    /// <param name="options">
    /// Export options controlling chunk byte and row limits. When null, built-in defaults apply
    /// (32 MiB byte limit, 100 000 row limit).
    /// </param>
    /// <param name="columnNames">
    /// The column names in ordinal order. Used to name blob containers per column.
    /// When null, columns are named <c>col0</c>, <c>col1</c>, etc.
    /// </param>
    /// <param name="columnSegments">
    /// The resolved path segment of each column in ordinal order, from
    /// <see cref="NameEncoder.ResolveCollisions"/>. Two columns differing only by case get different
    /// segments, hence different container families (design §3); when null the segment is encoded from
    /// the name alone, which cannot resolve a case collision.
    /// </param>
    public CsvRowWriter(
        string tableDir,
        ExportOptions? options,
        IReadOnlyList<string>? columnNames = null,
        IReadOnlyList<string>? columnSegments = null)
    {
        _tableDir = tableDir;
        _maxChunkBytes = options?.ChunkBytes ?? 32 * 1024 * 1024;
        _maxRowsPerChunk = options?.MaxRowsPerChunk ?? 100_000;
        _columnNames = columnNames ?? [];
        _columnSegments = columnSegments ?? [];
    }

    /// <summary>Gets the chunk descriptors recorded so far.</summary>
    public IReadOnlyList<TableChunkInfo> Chunks => _chunks;

    /// <summary>
    /// Writes a single row to the current chunk file. When the chunk reaches its byte or row
    /// limit, the chunk is sealed and a new one is started before returning.
    /// </summary>
    /// <param name="row">The row values to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when a chunk boundary was crossed (a chunk was finalized).</returns>
    public async Task<bool> WriteRowAsync(DataValue[] row, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_chunkStarted)
        {
            await OpenChunkAsync(ct);
        }

        // Process binary values: those above the threshold go to blob containers.
        // The row values are modified in place for serialization — the reference replaces the hex.
        var values = new string?[row.Length];
        for (var i = 0; i < row.Length; i++)
        {
            var dv = row[i];
            if (dv.IsNull)
            {
                values[i] = null;
            }
            else if (dv.LogicalType == ELogicalType.Binary
                && dv.Value is not null
                && _blobWriter is not null)
            {
                var columnName = i < _columnNames.Count ? _columnNames[i] : $"col{i}";
                var columnSegment = i < _columnSegments.Count
                    ? _columnSegments[i]
                    : NameEncoder.EncodeSegment(columnName);
                var blobRef = await _blobWriter.WriteBlobAsync(
                    dv.Value,
                    columnSegment,
                    _rowsInChunk,
                    ct);
                values[i] = BlobContainerWriter.FormatReference(blobRef);
                _blobBytesInChunk += blobRef.Length;

                if (!_blobFormatCounts.TryGetValue(columnName, out var suffixCounts))
                {
                    suffixCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    _blobFormatCounts[columnName] = suffixCounts;
                }
                if (!suffixCounts.TryGetValue(blobRef.Suffix, out var count))
                {
                    count = 0;
                }
                suffixCounts[blobRef.Suffix] = count + 1;
            }
            else
            {
                values[i] = dv.Value;
            }
        }

        // Serialize once as a CSV record — the only serialization this row receives.
        var record = new StringBuilder();
        CsvField.WriteRecord(record, values);
        await WriteRecordAsync(record.ToString(), ct);

        _rowsInChunk++;

        // Chunk boundary: sealed when the byte or row limit is reached.
        // Payload = row-file bytes + container bytes for this chunk.
        // The row limit is the configured one capped by the Excel ceiling (part A/R3).
        var rowLimit = Math.Min(_maxRowsPerChunk, ExcelRowCeiling);
        if (_bytesInChunk + _blobBytesInChunk >= _maxChunkBytes || _rowsInChunk >= rowLimit)
        {
            await FinalizeChunkAsync(ct);
            await OpenChunkAsync(ct);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Seals the current chunk if one has been started.
    /// </summary>
    public async Task FinishAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_chunkStarted)
        {
            await FinalizeChunkAsync(ct);
        }
    }

    private async Task OpenChunkAsync(CancellationToken ct)
    {
        var chunkPath = Path.Combine(_tableDir, ChunkFileName(_chunkIndex));
        var stream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _hash.AppendData(Utf8Preamble);
        _rowsInChunk = 0;
        _bytesInChunk = Utf8Preamble.Length;
        _blobBytesInChunk = 0;
        _chunkStarted = true;

        // Every chunk is independently readable: the first record is the complete column header.
        var header = _columnNames.Select(name => (string?)name).ToArray();
        var headerRecord = new StringBuilder();
        CsvField.WriteRecord(headerRecord, header);
        await WriteRecordAsync(headerRecord.ToString(), ct);

        // Create a new blob writer for this chunk
        _blobWriter = new BlobContainerWriter(
            _tableDir,
            ChunkFileStem(_chunkIndex));

        await Task.CompletedTask;
    }

    private async Task WriteRecordAsync(string record, CancellationToken ct)
    {
        await _writer!.WriteAsync(record.AsMemory(), ct);

        var bytes = Utf8NoBom.GetBytes(record);
        _hash!.AppendData(bytes);
        _bytesInChunk += bytes.Length;
    }

    private async Task FinalizeChunkAsync(CancellationToken ct)
    {
        await _writer!.FlushAsync(ct);
        await _writer.DisposeAsync();
        _writer = null;

        var chunkPath = Path.Combine(_tableDir, ChunkFileName(_chunkIndex));
        var hashBytes = _hash!.GetHashAndReset();
        var hash = Convert.ToHexStringLower(hashBytes);

        var fileInfo = new FileInfo(chunkPath);
        _chunks.Add(new TableChunkInfo
        {
            File = ChunkFileName(_chunkIndex),
            Bytes = fileInfo.Length,
            Rows = _rowsInChunk,
            Sha256 = hash,
        });

        // Seal blob containers for this chunk
        if (_blobWriter is not null)
        {
            var containers = await _blobWriter.SealAsync(ct);
            _allBlobContainers.AddRange(containers);
            await _blobWriter.DisposeAsync();
            _blobWriter = null;
        }

        _hash.Dispose();
        _hash = null;
        _chunkIndex++;
        _chunkStarted = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_writer is not null)
        {
            await _writer.DisposeAsync();
            _writer = null;
        }

        if (_blobWriter is not null)
        {
            await _blobWriter.DisposeAsync();
            _blobWriter = null;
        }

        _hash?.Dispose();
        _hash = null;
    }

    /// <summary>Gets all blob container files created during writing.</summary>
    public IReadOnlyList<BlobContainerInfo> BlobContainers => _allBlobContainers;

    /// <summary>
    /// Gets the blob format counts per Binary column. Keyed by column name, each value maps
    /// format suffix (e.g. "jpg", "png", "bin") to count of values stored in containers.
    /// </summary>
    /// <remarks>
    /// Tracked at write time: each value that crosses the threshold increments the count for
    /// its column and detected format suffix. Bounded by the naming table size, not by the row count.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> BlobFormatCounts
    {
        get
        {
            return _blobFormatCounts.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(kvp.Value, StringComparer.Ordinal));
        }
    }

    private static string ChunkFileName(int index) => $"data.{index + 1:D5}.csv";

    private static string ChunkFileStem(int index) => $"data.{index + 1:D5}";
}
