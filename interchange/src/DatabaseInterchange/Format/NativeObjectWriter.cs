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
        var json = JsonSerializer.Serialize(objects, CatalogJson.WriterOptions);
        await File.WriteAllTextAsync(filePath, json, System.Text.Encoding.UTF8, ct);

        return filePath;
    }
}
