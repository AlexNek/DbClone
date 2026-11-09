using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DatabaseInterchange.Format;

/// <summary>
/// Reads catalog JSON files from their streams, so a stored document is never one <see cref="string"/> and a
/// section's objects are produced one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Part A/R4 states the rule: no section, manifest, catalog or <c>table.json</c> is ever one string in either
/// direction. The two operations here respect it in the two ways the two shapes allow. A document that the
/// format keeps small by construction — the manifest, the catalog index, one table's <c>table.json</c>, the
/// security section — is deserialized from the file's stream, so its text exists only as the bytes the parser
/// buffers. A section, whose object count is the database's, is framed element by element
/// (<see cref="JsonArrayFramer"/>): one object's bytes are held, parsed, and released before the next is read.
/// </para>
/// <para>
/// The byte-order mark the older writers emitted is stepped over here rather than stripped from a buffer,
/// because a stream has no buffer to trim: the position is rewound when the file turns out not to begin with
/// one, which costs nothing on a seekable file handle.
/// </para>
/// </remarks>
internal static class JsonFile
{
    private const int StreamBufferSize = 64 * 1024;

    /// <summary>
    /// Opens a catalog JSON file for reading, positioned after any byte-order mark.
    /// </summary>
    public static async Task<FileStream> OpenReadAsync(string path, CancellationToken ct)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferSize,
            FileOptions.SequentialScan);

        byte[] header = new byte[3];
        var read = await stream.ReadAsync(header, ct);
        var hasBom = read == 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF;
        stream.Position = hasBom ? 3 : 0;

        return stream;
    }

    /// <summary>
    /// Deserializes a whole document the format keeps small by construction: the manifest, the catalog
    /// index and a table's own <c>table.json</c>.
    /// </summary>
    /// <remarks>
    /// Each of these is bounded — the manifest by section and table count, <c>table.json</c> by the
    /// naming table and a handful of scalars (design §8) — which is why a single document read is the
    /// right call for them while a section, whose object count is the database's, is not.
    /// </remarks>
    public static async Task<T?> ReadDocumentAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = await OpenReadAsync(path, ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, CatalogJson.ReaderOptions, ct);
    }

    /// <summary>
    /// Enumerates the elements of one array property of a section file, object by object.
    /// </summary>
    /// <remarks>
    /// The array's elements are framed straight out of the file's bytes, so a section holding a million
    /// columns is handled a column at a time: only the element being deserialized is in memory, and the file's
    /// text is never assembled — not as a string, and not as a whole document to walk.
    /// </remarks>
    /// <param name="path">The section file to read.</param>
    /// <param name="arrayProperty">The property holding the objects, as the writer spelled it.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async IAsyncEnumerable<T> ReadObjectsAsync<T>(
        string path,
        string arrayProperty,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = await OpenReadAsync(path, ct);
        var framer = new JsonArrayFramer(stream, arrayProperty);

        while (await framer.MoveNextAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var value = JsonSerializer.Deserialize<T>(framer.CurrentElement.Span, CatalogJson.ReaderOptions);
            if (value is not null)
            {
                yield return value;
            }
        }
    }
}
