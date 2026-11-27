using System.Text.Json;

using DbAdapter.Definitions.Interchange;

namespace DatabaseInterchange.Format;

/// <summary>
/// Writes native (dialect-specific) objects to the catalog.
/// These objects cannot be converted between dialects and must be preserved as-is.
/// </summary>
public sealed class NativeObjectWriter
{
    /// <summary>
    /// Writes native objects to the catalog.
    /// </summary>
    /// <param name="catalogPath">The catalog directory path.</param>
    /// <param name="dialect">The source dialect (e.g., "postgresql").</param>
    /// <param name="objects">The native objects to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file path of the written native objects file.</returns>
    public async Task<string> WriteNativeObjectsAsync(
        string catalogPath,
        string dialect,
        IReadOnlyList<NativeObjectDefinition> objects,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dialect);
        ArgumentNullException.ThrowIfNull(objects);

        var nativeDir = Path.Combine(catalogPath, InterchangeFormat.NativeDirectory);
        Directory.CreateDirectory(nativeDir);

        var filePath = Path.Combine(nativeDir, $"{dialect}.json");

        // The document goes into the file's own stream instead of being serialized to a string and written
        // out, which is the rule part A/R4 states for every catalog file. The native file is the one shape
        // the format keeps as a bare array rather than a section, so it is written in one call — but still
        // never as one assembled string.
        await using (var stream = new FileStream(
                         filePath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, objects, CatalogJson.WriterOptions, ct);
        }

        return filePath;
    }
}
