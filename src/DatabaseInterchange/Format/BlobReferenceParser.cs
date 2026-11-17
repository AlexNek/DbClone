namespace DatabaseInterchange.Format;

/// <summary>
/// Parses and validates blob references in the form <c>@blob:&lt;path&gt;:&lt;offset&gt;:&lt;length&gt;</c>.
/// </summary>
/// <remarks>
/// <para>A blob reference is recognized only in a column whose recorded logical type is
/// <c>Binary</c>. A reference in a non-Binary column is a format error.</para>
/// <para>The reference is self-describing: the path names the container file (whose suffix is
/// the detected format), and the offset and length name the exact range within it.</para>
/// <para>
/// The grammar and the path rule live here; the byte-dependent rules — the range fitting inside the
/// container (§6.3) and the name matching the bytes (§6.8) — are the reader's, because they need the
/// file open. <see cref="BinaryFieldGrammar"/> ties them to a column's type.
/// </para>
/// </remarks>
internal static class BlobReferenceParser
{
    /// <summary>The prefix that marks a field as a blob reference.</summary>
    internal const string Prefix = "@blob:";

    /// <summary>The directory a container always lives in, relative to the table folder.</summary>
    internal const string ContainerDirectory = "blobs/";

    /// <summary>
    /// Tries to parse a blob reference from a field value.
    /// </summary>
    /// <param name="field">The raw field value from the row file.</param>
    /// <param name="reference">The parsed reference, or null if the field is not a reference.</param>
    /// <returns>True if the field is a valid blob reference; false if it is not a reference.</returns>
    /// <exception cref="InterchangeFormatException">
    /// The field starts with <c>@blob:</c> but is malformed.
    /// </exception>
    internal static bool TryParse(string field, out BlobRef? reference)
    {
        reference = null;

        if (!field.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Parse: @blob:<path>:<offset>:<length>
        // The path may contain colons (it's a relative path), so we split from the right.
        var rest = field[Prefix.Length..];
        var lastColon = rest.LastIndexOf(':');
        if (lastColon < 0)
        {
            throw new InterchangeFormatException(
                $"Malformed blob reference: missing length separator. Field: \"{Truncate(field)}\".");
        }

        var lengthStr = rest[(lastColon + 1)..];
        var beforeLength = rest[..lastColon];

        var secondLastColon = beforeLength.LastIndexOf(':');
        if (secondLastColon < 0)
        {
            throw new InterchangeFormatException(
                $"Malformed blob reference: missing offset separator. Field: \"{Truncate(field)}\".");
        }

        var offsetStr = beforeLength[(secondLastColon + 1)..];
        var relativePath = beforeLength[..secondLastColon];

        // Validate path
        ValidatePath(relativePath);

        // Parse offset and length
        if (!long.TryParse(offsetStr, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var offset) || offset < 0)
        {
            throw new InterchangeFormatException(
                $"Malformed blob reference: invalid offset \"{offsetStr}\". Field: \"{Truncate(field)}\".");
        }

        if (!int.TryParse(lengthStr, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 0)
        {
            throw new InterchangeFormatException(
                $"Malformed blob reference: invalid length \"{lengthStr}\". Field: \"{Truncate(field)}\".");
        }

        reference = new BlobRef(relativePath, offset, length);
        return true;
    }

    /// <summary>
    /// Extracts the format suffix from a blob reference's relative path.
    /// </summary>
    /// <param name="relativePath">The relative path from the reference.</param>
    /// <returns>The file extension without the leading dot, or "bin" if none.</returns>
    internal static string ExtractSuffix(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        if (string.IsNullOrEmpty(ext) || ext.Length <= 1)
        {
            return BlobFormat.Fallback;
        }

        return ext[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Resolves a reference's relative path to the container's full path inside <paramref name="tableDir"/>.
    /// </summary>
    /// <remarks>
    /// The path rule of design §6.2, stated for a reference rather than for a name: the container lives in
    /// its own table's folder and nowhere else. The string checks run before anything is opened, so a
    /// crafted path cannot reach outside the catalog; the reparse-point check runs after, because only a
    /// path that exists can be one. A link or junction placed in <c>blobs/</c> after the export would send
    /// the restore's reads to bytes the catalog never recorded, so it is refused as the corruption it is.
    /// </remarks>
    /// <param name="reference">The parsed reference.</param>
    /// <param name="tableDir">The table's data directory, as the catalog index resolved it.</param>
    /// <returns>The absolute path of the container file the reference names.</returns>
    /// <exception cref="InterchangeFormatException">
    /// The path leaves the table folder or the container is reached through a reparse point.
    /// </exception>
    internal static string ResolveContainerPath(BlobRef reference, string tableDir)
    {
        ValidatePath(reference.RelativePath);

        var containerPath = Path.GetFullPath(Path.Combine(
            tableDir,
            reference.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        // GetFullPath already folds ".." away, so comparing the resolved prefix is what proves the path
        // never left this table's folder — a symlinked catalog root included, since both sides are full.
        var tableRoot = Path.GetFullPath(tableDir + Path.DirectorySeparatorChar);
        if (!containerPath.StartsWith(tableRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InterchangeFormatException(
                $"Blob reference resolves outside its table folder: \"{reference.RelativePath}\".");
        }

        RejectReparsePoint(containerPath, reference.RelativePath);
        RejectReparsePoint(Path.GetDirectoryName(containerPath)!, reference.RelativePath);

        return containerPath;
    }

    private static void RejectReparsePoint(string path, string relativePath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (IOException)
        {
            // A missing file is reported by the reader that tries to open it, with the container's own
            // message; here there is nothing to test and nothing to invent.
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InterchangeFormatException(
                $"Blob reference resolves through a reparse point: \"{relativePath}\".");
        }
    }

    /// <summary>
    /// Validates that a relative path is safe: no absolute paths, no drive letters, no ".."
    /// segments, and inside the table folder's <c>blobs/</c> directory.
    /// </summary>
    private static void ValidatePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            throw new InterchangeFormatException("Blob reference has an empty path.");
        }

        // No absolute paths
        if (Path.IsPathRooted(relativePath))
        {
            throw new InterchangeFormatException(
                $"Blob reference path must be relative: \"{relativePath}\".");
        }

        // No drive letters
        if (relativePath.Length >= 2 && relativePath[1] == ':')
        {
            throw new InterchangeFormatException(
                $"Blob reference path must not contain a drive letter: \"{relativePath}\".");
        }

        // No ".." segments
        var segments = relativePath.Split('/', '\\');
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                throw new InterchangeFormatException(
                    $"Blob reference path must not contain \"..\" segments: \"{relativePath}\".");
            }
        }

        // Must be under "blobs/"
        if (!relativePath.StartsWith(ContainerDirectory, StringComparison.Ordinal))
        {
            throw new InterchangeFormatException(
                $"Blob reference path must start with \"{ContainerDirectory}\": \"{relativePath}\".");
        }
    }

    private static string Truncate(string value, int max = 60) =>
        value.Length <= max ? value : value[..max] + "…";
}
