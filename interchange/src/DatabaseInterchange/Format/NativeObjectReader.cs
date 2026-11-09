using System.Text.Json;

using DbAdapter.Definitions.Interchange;

namespace DatabaseInterchange.Format;

/// <summary>
/// Reads native (dialect-specific) objects from the catalog.
/// </summary>
public sealed class NativeObjectReader
{
    /// <summary>
    /// Reads native objects for the specified dialect.
    /// </summary>
    /// <param name="catalogPath">The catalog directory path.</param>
    /// <param name="dialect">The dialect to read (e.g., "postgresql").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The native objects, or empty list if not found.</returns>
    public async Task<IReadOnlyList<NativeObjectDefinition>> ReadNativeObjectsAsync(
        string catalogPath,
        string dialect,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dialect);

        var filePath = Path.Combine(catalogPath, InterchangeFormat.NativeDirectory, $"{dialect}.json");
        if (!File.Exists(filePath))
        {
            return [];
        }

        // Read from the file's stream, so the document's text is never assembled into one string (R4). A
        // stream that begins with the older writer's byte-order mark is stepped past by JsonFile.
        await using var stream = await JsonFile.OpenReadAsync(filePath, ct);
        var objects = await JsonSerializer.DeserializeAsync<List<NativeObjectDefinition>>(
            stream, CatalogJson.ReaderOptions, ct);
        return objects ?? [];
    }

    /// <summary>
    /// Checks if native objects exist for the specified dialect.
    /// </summary>
    public static bool HasNativeObjects(string catalogPath, string dialect)
    {
        var filePath = Path.Combine(catalogPath, InterchangeFormat.NativeDirectory, $"{dialect}.json");
        return File.Exists(filePath);
    }
}
