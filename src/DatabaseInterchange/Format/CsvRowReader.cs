using System.Runtime.CompilerServices;
using System.Text;
using CanonicalModel.Interchange;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Format;

/// <summary>
/// Streams rows from a UTF-8 CSV chunk file, one record at a time.
/// </summary>
/// <remarks>
/// Every chunk begins with its complete column header. Records are parsed from the stream rather than
/// by physical lines, so quoted fields may contain commas, CR, LF, or CRLF without changing row boundaries.
/// </remarks>
internal sealed class CsvRowReader : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly BinaryFieldGrammar _grammar;
    private readonly IReadOnlyList<ELogicalType> _columnTypes;
    private readonly IReadOnlyList<string> _columnNames;
    private readonly char[] _buffer = new char[4096];
    private int _bufferPosition;
    private int _bufferLength;
    private bool _headerRead;
    private bool _disposed;

    /// <summary>
    /// Opens a chunk file for streaming row reads.
    /// </summary>
    /// <param name="chunkPath">The absolute path to the <c>.csv</c> chunk file.</param>
    /// <param name="grammar">The table's field grammar.</param>
    /// <param name="columnTypes">The logical type of each column in ordinal order.</param>
    /// <param name="columnNames">The column names expected in the chunk header.</param>
    public CsvRowReader(
        string chunkPath,
        BinaryFieldGrammar grammar,
        IReadOnlyList<ELogicalType> columnTypes,
        IReadOnlyList<string> columnNames)
    {
        var stream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        _grammar = grammar;
        _columnTypes = columnTypes;
        _columnNames = columnNames;
    }

    /// <summary>
    /// Reads the next CSV record as typed values, or <see langword="null"/> when the chunk is exhausted.
    /// </summary>
    public async Task<DataValue[]?> ReadRecordAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_headerRead)
        {
            var header = await ReadCsvRecordAsync(ct);
            if (header is null)
            {
                throw new InterchangeFormatException("A CSV chunk has no column header.");
            }

            if (!header.SequenceEqual(_columnNames, StringComparer.Ordinal))
            {
                throw new InterchangeFormatException(
                    $"CSV chunk header does not match the table columns. Expected [{string.Join(", ", _columnNames)}], " +
                    $"got [{string.Join(", ", header.Select(value => value ?? "\\N"))}].");
            }

            _headerRead = true;
        }

        var fields = await ReadCsvRecordAsync(ct);
        if (fields is null)
        {
            return null;
        }

        if (fields.Count != _columnTypes.Count)
        {
            throw new InterchangeFormatException(
                $"CSV row has {fields.Count} field(s), but the table header declares {_columnTypes.Count} column(s).");
        }

        return await _grammar.DecodeRowAsync(fields.ToArray(), _columnTypes, ct);
    }

    /// <summary>
    /// Enumerates all records in the chunk as decoded rows.
    /// </summary>
    public async IAsyncEnumerable<DataValue[]> ReadAllRecordsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var record = await ReadRecordAsync(ct);
            if (record is null)
            {
                yield break;
            }

            yield return record;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _reader.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<List<string?>?> ReadCsvRecordAsync(CancellationToken ct)
    {
        var fields = new List<string?>();
        var field = new StringBuilder();
        var fieldStarted = false;
        var quoted = false;
        var inQuotes = false;
        var afterClosingQuote = false;
        var hasInput = false;
        var pending = -1;

        while (true)
        {
            var value = pending >= 0 ? pending : await ReadCharAsync(ct);
            pending = -1;

            if (value < 0)
            {
                if (inQuotes)
                {
                    throw new InterchangeFormatException("A CSV field starts with a quote but has no closing quote.");
                }

                if (!hasInput && fields.Count == 0 && field.Length == 0 && !fieldStarted)
                {
                    return null;
                }

                AddField();
                return fields;
            }

            hasInput = true;
            var c = (char)value;

            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                    continue;
                }

                var next = await ReadCharAsync(ct);
                if (next == '"')
                {
                    field.Append('"');
                    continue;
                }

                inQuotes = false;
                afterClosingQuote = true;
                pending = next;
                continue;
            }

            if (afterClosingQuote)
            {
                if (c == ',')
                {
                    AddField();
                    ResetField();
                    continue;
                }

                if (c == '\n')
                {
                    AddField();
                    return fields;
                }

                if (c == '\r')
                {
                    var next = await ReadCharAsync(ct);
                    if (next != '\n') pending = next;
                    AddField();
                    return fields;
                }

                throw new InterchangeFormatException("Unexpected characters after a closing quote in a CSV field.");
            }

            if (!fieldStarted && c == '"')
            {
                fieldStarted = true;
                quoted = true;
                inQuotes = true;
                continue;
            }

            if (c == ',')
            {
                AddField();
                ResetField();
                continue;
            }

            if (c == '\n')
            {
                AddField();
                return fields;
            }

            if (c == '\r')
            {
                var next = await ReadCharAsync(ct);
                if (next != '\n') pending = next;
                AddField();
                return fields;
            }

            fieldStarted = true;
            field.Append(c);
        }

        void AddField()
        {
            var text = field.ToString();
            fields.Add(quoted ? text : CsvField.Decode(text));
        }

        void ResetField()
        {
            field.Clear();
            fieldStarted = false;
            quoted = false;
            afterClosingQuote = false;
        }
    }

    private async ValueTask<int> ReadCharAsync(CancellationToken ct)
    {
        if (_bufferPosition >= _bufferLength)
        {
            _bufferLength = await _reader.ReadAsync(_buffer.AsMemory(), ct);
            _bufferPosition = 0;
            if (_bufferLength == 0) return -1;
        }

        return _buffer[_bufferPosition++];
    }
}
