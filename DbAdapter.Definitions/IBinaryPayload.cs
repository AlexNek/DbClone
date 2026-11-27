namespace DbAdapter.Definitions;

/// <summary>
/// A binary value carried out-of-band, as bytes, instead of as text.
/// </summary>
/// <remarks>
/// <para>
/// The interchange model stores a large binary value outside the row file and keeps a reference to the exact
/// byte range in its place. When such a value is read back, the reader hands it over as this payload rather
/// than as a hex string, so the writer can bind the bytes to the target without ever putting hex inside SQL
/// text and without the value becoming one <see cref="string"/> in either direction.
/// </para>
/// <para>
/// The contract names no engine, no storage layout and no wire type: it says only that the bytes exist, how
/// many there are, and how to read them. How the producer reaches them — a range of a container file, a
/// memory buffer, anything else — is its own business.
/// </para>
/// </remarks>
public interface IBinaryPayload
{
    /// <summary>Gets the number of bytes the payload holds.</summary>
    long Length { get; }

    /// <summary>
    /// Opens a read-only stream over the payload's bytes, positioned at its first byte.
    /// </summary>
    /// <remarks>
    /// Each call returns an independent stream, so two callers may read the same payload at the same time.
    /// The caller disposes the stream it was given.
    /// </remarks>
    /// <returns>A readable, seekable stream over the payload.</returns>
    Stream OpenRead();
}
