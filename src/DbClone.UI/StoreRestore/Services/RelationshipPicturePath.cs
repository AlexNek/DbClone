using System.IO;

namespace DbClone.UI.StoreRestore.Services;

/// <summary>
/// Where a catalog's relationship picture lands (D5): inside the catalog folder, named after it, beside
/// the product's own <c>summary.md</c> — so a catalog and the picture of it travel as one folder.
/// </summary>
/// <remarks>
/// A file in the catalog's root is not part of the catalog: the manifest never lists it, it is never
/// hashed and never restored, and the verifier's "nothing unlisted sits here" rule is scoped to
/// <c>data/&lt;schema&gt;/&lt;table&gt;/</c> — the ground <c>summary.md</c> already stands on. Deleting
/// the picture, editing it or replacing it with nonsense cannot break the catalog.
/// </remarks>
internal static class RelationshipPicturePath
{
    /// <summary>Gets the picture path for a catalog: the catalog folder, its own name, <c>.html</c>.</summary>
    /// <param name="catalogPath">The absolute path of a catalog folder.</param>
    /// <returns>The absolute path of the document that describes <paramref name="catalogPath"/>.</returns>
    /// <exception cref="ArgumentException">The path is empty or names no folder of its own to sit in.</exception>
    internal static string For(string catalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        var catalogDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(catalogPath));
        var name = Path.GetFileName(catalogDirectory);

        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException(
                $"A catalog needs a folder of its own before its picture has somewhere to go: {catalogPath}",
                nameof(catalogPath));
        }

        return Path.Combine(catalogDirectory, name + ".html");
    }
}
