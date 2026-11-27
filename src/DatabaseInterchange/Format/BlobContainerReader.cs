namespace DatabaseInterchange.Format;

/// <summary>
/// Verifies blob references against the containers they name.
/// </summary>
/// <remarks>
/// <para>
/// The reader answers one question: is the address this row file states still true of the bytes on disk? It
/// checks the path, range, and format signature, then hands back the resolved path. Reading the value itself is
/// not this class's job: the caller hands the target a <see cref="BlobPayload"/> over the file.
/// </para>
/// <para>
/// Containers are read forward: one open handle per container, seek only when a value is examined, and the probe
/// reads at most <see cref="BlobFormat.ProbeBytes"/> bytes. A verification pass therefore costs a seek and a
/// small read per value, whatever the value's size.
/// </para>
/// <para>
/// The suffix is never consulted to decide how to bind a value — a <c>bin</c> container restores exactly like a
/// <c>jpg</c> one (§6.9). It is consulted only to ask whether the name still describes the bytes under it.
/// </para>
/// </remarks>
internal sealed class BlobContainerReader : IAsyncDisposable
{
    private readonly string _tableDir;
    private readonly Dictionary<string, FileStream> _openStreams = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Initializes a new blob container reader for the specified table directory.
    /// </summary>
    /// <param name="tableDir">
    /// The table's data directory (e.g. <c>data/public/images</c>). Container paths in
    /// references are relative to this directory.
    /// </param>
    public BlobContainerReader(string tableDir)
    {
        _tableDir = tableDir;
    }

    /// <summary>
    /// Verifies that a reference still addresses real bytes, and returns the container's absolute path.
    /// </summary>
    /// <param name="reference">The parsed blob reference (path, offset, length).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The absolute path of the verified container.</returns>
    /// <exception cref="InterchangeFormatException">
    /// The path leaves the table folder, resolves through a reparse point, names a missing file, or
    /// addresses a range the file does not hold.
    /// </exception>
    /// <exception cref="BlobFormatMismatchException">
    /// The suffix the path carries is not the signature the bytes at the range hold (§6.8).
    /// </exception>
    public async Task<string> VerifyAsync(
        BlobRef reference,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // §6.2 — the path rule: inside this table's folder, never through a reparse point.
        var containerPath = BlobReferenceParser.ResolveContainerPath(reference, _tableDir);

        var stream = GetOrOpenStream(containerPath, reference.RelativePath);

        // §6.3 — the range must exist inside the container.
        if (reference.Offset + reference.Length > stream.Length)
        {
            throw new InterchangeFormatException(
                $"Blob reference overruns container: path=\"{reference.RelativePath}\", " +
                $"offset={reference.Offset}, length={reference.Length}, container size={stream.Length}.");
        }

        // Every non-NULL Binary value is written to its own file, so a valid reference may have any length,
        // including zero.

        // §6.8 — the name against the bytes. Read the probe through the shared handle.
        stream.Seek(reference.Offset, SeekOrigin.Begin);
        var probeLength = Math.Min(reference.Length, BlobFormat.ProbeBytes);
        var probe = await ReadExactAsync(stream, probeLength, reference, ct);
        var suffix = BlobReferenceParser.ExtractSuffix(reference.RelativePath);
        if (!BlobFormat.NameMatchesBytes(suffix, probe, probeLength))
        {
            throw new BlobFormatMismatchException(
                $"Blob container \"{reference.RelativePath}\" is named \"{suffix}\" but the bytes at " +
                $"offset {reference.Offset} do not carry that signature.");
        }

        return containerPath;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var stream in _openStreams.Values)
        {
            await stream.DisposeAsync();
        }

        _openStreams.Clear();
    }

    private static async Task<byte[]> ReadExactAsync(
        FileStream stream,
        int count,
        BlobRef reference,
        CancellationToken ct)
    {
        var bytes = new byte[count];
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0)
            {
                throw new InterchangeFormatException(
                    $"Unexpected end of container: path=\"{reference.RelativePath}\", " +
                    $"expected {count} bytes at offset {reference.Offset}, got {totalRead}.");
            }

            totalRead += read;
        }

        return bytes;
    }

    private FileStream GetOrOpenStream(string containerPath, string relativePath)
    {
        if (_openStreams.TryGetValue(containerPath, out var existing))
        {
            return existing;
        }

        if (!File.Exists(containerPath))
        {
            throw new InterchangeFormatException(
                $"Blob container not found: \"{relativePath}\".");
        }

        var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _openStreams[containerPath] = stream;
        return stream;
    }
}
