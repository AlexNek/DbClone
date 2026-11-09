namespace DatabaseInterchange.Format;

/// <summary>
/// A parsed blob reference: the relative path, offset, and length within a container file.
/// </summary>
/// <param name="RelativePath">Path relative to the table folder (forward slashes).</param>
/// <param name="Offset">Byte offset within the container file.</param>
/// <param name="Length">Byte length of the value within the container.</param>
internal sealed record BlobRef(
    string RelativePath,
    long Offset,
    int Length);
