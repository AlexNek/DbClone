using DbAdapter.Definitions;

namespace DatabaseInterchange.Format;

/// <summary>
/// The bytes a blob reference points at, handed over as a payload instead of as text.
/// </summary>
/// <remarks>
/// <para>
/// The payload carries an already validated address: the path resolved inside its own table's folder, the range
/// fits the container and the container's name still matches its bytes (§6.2, §6.3, §6.8). Nothing here re-checks
/// that, and nothing here reads the whole value — <see cref="OpenRead"/> hands out a window over the file, so the
/// bytes reach the target through a buffer-sized stream rather than through one string.
/// </para>
/// <para>
/// Each call opens its own handle, so two consumers may read the same value at the same time and the payload stays
/// valid after the reader that produced it is disposed. The caller disposes the stream it was given.
/// </para>
/// </remarks>
internal sealed class BlobPayload(string containerPath, long offset, int length) : IBinaryPayload
{
    private readonly string _containerPath = containerPath;
    private readonly long _offset = offset;
    private readonly int _length = length;

    /// <summary>Gets the detected file suffix carried by the container name.</summary>
    internal string Suffix => Path.GetExtension(_containerPath).TrimStart('.') is { Length: > 0 } suffix
        ? suffix
        : "bin";

    /// <inheritdoc />
    public long Length => _length;

    /// <inheritdoc />
    public Stream OpenRead()
    {
        var source = new FileStream(
            _containerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new BlobRangeStream(source, _offset, _length, ownsSource: true);
    }
}
