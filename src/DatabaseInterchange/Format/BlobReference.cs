namespace DatabaseInterchange.Format;

/// <summary>
/// A value the writer put into an out-of-band container, together with what the row file has to keep.
/// </summary>
/// <remarks>
/// <see cref="Suffix"/> is the format the bytes were named by, carried here only so the row writer can
/// count values per format for <c>table.json</c>. Nothing on the read side consults it (§6.9).
/// </remarks>
/// <param name="RelativePath">Path relative to the table folder (forward slashes).</param>
/// <param name="Offset">Byte offset within the container file.</param>
/// <param name="Length">Byte length of the value within the container.</param>
/// <param name="Suffix">The detected format suffix (e.g. "jpg", "png", "bin").</param>
internal sealed record BlobReference(
    string RelativePath,
    long Offset,
    int Length,
    string Suffix);
