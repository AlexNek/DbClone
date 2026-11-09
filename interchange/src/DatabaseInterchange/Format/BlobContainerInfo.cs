namespace DatabaseInterchange.Format;

/// <summary>
/// What a sealed blob container states about itself: its name, the bytes it holds and the hash of
/// exactly those bytes.
/// </summary>
/// <remarks>
/// The length and hash are recorded while the values are written, not re-derived from the closed file
/// (part A R8): a container whose hash comes from the write pass is a statement about the bytes the
/// references cover, which is what §6.6 then checks.
/// </remarks>
/// <param name="FileName">The container file name, without the <c>blobs/</c> prefix.</param>
/// <param name="Bytes">The total bytes in the container.</param>
/// <param name="Sha256">The SHA-256 hash of the container, lowercase hex.</param>
internal sealed record BlobContainerInfo(
    string FileName,
    long Bytes,
    string Sha256);
