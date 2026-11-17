namespace DatabaseInterchange.Format;

/// <summary>
/// Presents a fixed byte range of another stream as a stream of its own.
/// </summary>
/// <remarks>
/// <para>
/// A stored binary value is one range inside a container that holds many values, so handing the target engine
/// "this value" means handing it a window rather than the file. The window reports the range as its length and
/// stops at its end, which is what lets a restore bind the bytes without reading the neighbours' bytes and
/// without ever holding the value as one buffer.
/// </para>
/// <para>
/// Reads are positioned on the underlying stream, so a shared source handle stays correct for concurrent
/// windows; in the restore path each payload opens its own source and this class owns it, disposing it with itself.
/// </para>
/// </remarks>
internal sealed class BlobRangeStream : Stream
{
    private readonly Stream _source;
    private readonly long _offset;
    private readonly long _length;
    private readonly bool _ownsSource;
    private long _position;
    private bool _disposed;
    private bool _sourceReleased;

    /// <summary>
    /// Windows <paramref name="source"/> to the range starting at <paramref name="offset"/>.
    /// </summary>
    /// <param name="source">The underlying stream; must be seekable.</param>
    /// <param name="offset">Where the range begins in the underlying stream.</param>
    /// <param name="length">How many bytes the range holds.</param>
    /// <param name="ownsSource">
    /// Whether this window disposes the underlying stream. A payload that opened its own handle passes
    /// <see langword="true"/>; a window over a shared, cached handle passes <see langword="false"/>.
    /// </param>
    public BlobRangeStream(Stream source, long offset, long length, bool ownsSource)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (!source.CanSeek)
        {
            throw new ArgumentException("A byte range needs a seekable source stream.", nameof(source));
        }

        _source = source;
        _offset = offset;
        _length = length;
        _ownsSource = ownsSource;
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("Seeking before the start of the stored range.");
        }

        _position = target;
        return _position;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int index, int count) =>
        Read(buffer.AsSpan(index, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var slice = buffer.Length < remaining ? buffer : buffer[..(int)remaining];
        _source.Position = _offset + _position;
        var read = _source.Read(slice);
        _position += read;
        return read;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return ValueTask.FromResult(0);
        }

        var slice = buffer.Length < remaining ? buffer : buffer[..(int)remaining];
        _source.Position = _offset + _position;
        return ReadAsyncCore(slice, cancellationToken);
    }

    private async ValueTask<int> ReadAsyncCore(Memory<byte> slice, CancellationToken cancellationToken)
    {
        var read = await _source.ReadAsync(slice, cancellationToken);
        _position += read;
        return read;
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("A stored range is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A stored range is read-only.");

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("A stored range is read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_sourceReleased)
        {
            _disposed = true;
            _sourceReleased = true;
            if (_ownsSource)
            {
                _source.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_sourceReleased)
        {
            _disposed = true;
            _sourceReleased = true;
            if (_ownsSource)
            {
                await _source.DisposeAsync();
            }
        }

        // The base implementation releases the source through Dispose(bool), which the guard above makes a
        // no-op once the async path has run.
        await base.DisposeAsync();
    }
}
