using System.Text;

namespace DatabaseInterchange.Format;

/// <summary>
/// Pulls the elements of one named JSON array out of a stream, one element at a time.
/// </summary>
/// <remarks>
/// <para>
/// A catalog section is a document whose <c>objects</c> array holds one entry per schema object, and the
/// number of entries is the database's, not a constant. Part A/R4 therefore forbids reading such a file as
/// one value: the whole text, or the whole object graph, must never exist at once. This framer is what makes
/// that readable — it walks the file's bytes in a fixed buffer, tracks the structure (strings, escapes,
/// container depth) itself, and reports each element's raw bytes as soon as the element ends. The bytes of
/// one element are held; the rest of the file stays on disk.
/// </para>
/// <para>
/// The structural walk is deliberately minimal: the only property name it looks for is the array it was given,
/// and only as a direct member of the root object, which is where the section writers put it. Everything else
/// is depth bookkeeping, so an element may contain any number of nested objects and arrays without the framer
/// mistaking them for the end of the section.
/// </para>
/// <para>
/// Because the state machine carries its position in fields rather than in a tokenizer state, a buffer refill
/// needs no shifting and no retry: the next byte is read into a fresh buffer and the walk continues exactly
/// where it stopped, including in the middle of an element.
/// </para>
/// </remarks>
internal sealed class JsonArrayFramer
{
    private const int BufferBytes = 64 * 1024;
    private const int InitialElementBytes = 4 * 1024;
    private const byte Quote = (byte)'"';
    private const byte Colon = (byte)':';
    private const byte Comma = (byte)',';
    private const byte OpenBrace = (byte)'{';
    private const byte CloseBrace = (byte)'}';
    private const byte OpenBracket = (byte)'[';
    private const byte CloseBracket = (byte)']';
    private const byte Backslash = (byte)'\\';
    private const byte Space = (byte)' ';
    private const byte Tab = (byte)'\t';
    private const byte CarriageReturn = (byte)'\r';
    private const byte LineFeed = (byte)'\n';

    /// <summary>
    /// The depth inside the root object, where a section's own properties live.
    /// </summary>
    private const int RootChildDepth = 1;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[BufferBytes];
    private readonly byte[] _propertyName;
    private byte[] _elementBuffer = new byte[InitialElementBytes];

    private int _filled;
    private int _cursor;
    private int _elementLength;
    private int _depth;
    private bool _inString;
    private bool _escaped;
    private bool _stringClosed;
    private int _propertyNameMatchLength;
    private bool _awaitingArray;
    private bool _insideArray;
    private int _arrayDepth;
    private bool _collecting;
    private byte _elementStart;
    private bool _done;

    /// <summary>
    /// Frames one array of the stream, starting at its current position.
    /// </summary>
    /// <param name="stream">The seekable file stream, already stepped past any byte-order mark.</param>
    /// <param name="arrayProperty">The root-object property whose array is enumerated, as the writer spelled it.</param>
    public JsonArrayFramer(Stream stream, string arrayProperty)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(arrayProperty);

        _stream = stream;
        _propertyName = Encoding.UTF8.GetBytes(arrayProperty);
    }

    /// <summary>
    /// Gets the bytes of the element the last successful <see cref="MoveNextAsync"/> produced.
    /// </summary>
    /// <remarks>
    /// A <see cref="ReadOnlyMemory{T}"/> rather than a span: the caller deserializes it between awaits, and a
    /// reference struct may not live across one.
    /// </remarks>
    public ReadOnlyMemory<byte> CurrentElement => new(_elementBuffer, 0, _elementLength);

    /// <summary>
    /// Advances to the next element of the array, reading from the stream as needed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> while elements remain; <see langword="false"/> once the array — or the file —
    /// is exhausted. A file that stops in the middle of an element hands over the partial bytes, so the
    /// caller's deserialization reports the truncation instead of the framer guessing at it.
    /// </returns>
    public async Task<bool> MoveNextAsync(CancellationToken ct)
    {
        while (!_done)
        {
            if (_cursor >= _filled)
            {
                if (!await RefillAsync(ct))
                {
                    _done = true;
                    if (!_collecting)
                    {
                        return false;
                    }

                    // A truncated file that ends inside an element still delivers those bytes; the
                    // deserializer then reports the incomplete value.
                    return true;
                }
            }

            if (Scan(_buffer[_cursor++]))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> RefillAsync(CancellationToken ct)
    {
        _cursor = 0;
        _filled = await _stream.ReadAsync(_buffer, ct);
        return _filled > 0;
    }

    /// <summary>
    /// Consumes one byte of the file, returning <see langword="true"/> when it closed an element.
    /// </summary>
    /// <remarks>
    /// Depth bookkeeping: <c>_arrayDepth</c> is the open-container count directly after the array's own
    /// <c>[</c>, so it is also the level at which the array's values sit. An element container closes when the
    /// count falls back to it; a comma or the array's <c>]</c> at that same level ends a scalar element.
    /// </remarks>
    private bool Scan(byte b)
    {
        // The array's own closing bracket and the commas between elements are structure, not element bytes.
        // An element that is itself an array ends on a bracket at the same depth the array ends on, so the
        // byte that opened the element is what tells the two apart.
        var closesArray = b == CloseBracket
            && _insideArray
            && _depth == _arrayDepth
            && _elementStart != OpenBracket;
        var endsElement = (b == Comma || closesArray) && _collecting && _depth == _arrayDepth;

        if (_insideArray && !_collecting && _depth == _arrayDepth && !IsSeparator(b))
        {
            _collecting = true;
            _elementStart = b;
            _elementLength = 0;
        }

        if (_collecting && !endsElement)
        {
            AppendElementByte(b);
        }

        if (_inString)
        {
            return ScanInsideString(b);
        }

        switch (b)
        {
            case Quote:
                _inString = true;
                _escaped = false;
                _propertyNameMatchLength = 0;
                _stringClosed = false;
                return false;

            case Colon:
                // The name of the array being framed is recognized only as a member of the root object, so a
                // property of the same spelling inside an element cannot redirect the walk.
                _awaitingArray = _stringClosed
                    && _depth == RootChildDepth
                    && _propertyNameMatchLength == _propertyName.Length;
                _stringClosed = false;
                return false;

            case OpenBrace or OpenBracket:
                _stringClosed = false;
                _depth++;
                if (b == OpenBracket && _awaitingArray)
                {
                    _awaitingArray = false;
                    _insideArray = true;
                    _arrayDepth = _depth;
                }

                return false;

            case CloseBrace:
                _stringClosed = false;
                _awaitingArray = false;
                _depth--;
                if (_collecting && _depth == _arrayDepth)
                {
                    FinalizeElement();
                    return true;
                }

                return false;

            case CloseBracket:
                _stringClosed = false;
                _awaitingArray = false;
                _depth--;
                if (closesArray)
                {
                    _insideArray = false;
                    _done = true;
                }

                // Two different endings land here: the bracket that closed an element which is itself an
                // array, and the bracket that closed the array after a scalar element (the comma case above
                // covers a scalar followed by more elements).
                if (endsElement || (_collecting && _depth == _arrayDepth))
                {
                    FinalizeElement();
                    return true;
                }

                return false;

            case Comma:
                _stringClosed = false;
                _awaitingArray = false;
                return endsElement && FinalizeElement();

            default:
                if (!IsWhiteSpace(b))
                {
                    _awaitingArray = false;
                }

                return false;
        }
    }

    /// <summary>
    /// Consumes a byte that belongs to the body of a JSON string.
    /// </summary>
    private bool ScanInsideString(byte b)
    {
        if (_escaped)
        {
            _escaped = false;
            return false;
        }

        if (b == Backslash)
        {
            _escaped = true;

            // An escaped name is not the plain spelling the section writers emit, so it stops matching
            // rather than being decoded here.
            _propertyNameMatchLength = -1;
            return false;
        }

        if (b == Quote)
        {
            _inString = false;
            _stringClosed = true;
            return false;
        }

        if (_propertyNameMatchLength >= 0)
        {
            if (_propertyNameMatchLength >= _propertyName.Length
                || b != _propertyName[_propertyNameMatchLength])
            {
                _propertyNameMatchLength = -1;
            }
            else
            {
                _propertyNameMatchLength++;
            }
        }

        return false;
    }

    private void AppendElementByte(byte b)
    {
        if (_elementLength == _elementBuffer.Length)
        {
            Array.Resize(ref _elementBuffer, _elementBuffer.Length * 2);
        }

        _elementBuffer[_elementLength++] = b;
    }

    private bool FinalizeElement()
    {
        _collecting = false;
        _elementStart = 0;
        return true;
    }

    private static bool IsSeparator(byte b) =>
        b == Comma || IsWhiteSpace(b);

    private static bool IsWhiteSpace(byte b) =>
        b is Space or Tab or CarriageReturn or LineFeed;
}
