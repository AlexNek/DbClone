using System.IO;
using System.Text;

namespace DbClone.UI.StoreRestore.Services;

/// <summary>
/// The store-time write (D12): the document a finished catalog carries lands inside that catalog, named
/// after it, whole or not at all. The view never writes here — a view is a string in memory.
/// </summary>
/// <remarks>
/// The file sits in the catalog's root, beside the product's own <c>summary.md</c>, and is not part of
/// the catalog: the manifest never lists it, it is never hashed and never restored.
/// </remarks>
internal sealed class RelationshipPictureWriter
{
    /// <summary>The suffix the document is assembled under before it is moved into place.</summary>
    private const string TemporarySuffix = ".tmp";

    /// <summary>UTF-8 without a byte-order mark, the encoding the product's own writers use.</summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes the document into the catalog it describes.</summary>
    /// <param name="catalogPath">The absolute path of the finished catalog folder.</param>
    /// <param name="document">The whole document, as text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The absolute path the picture landed on.</returns>
    internal async Task<string> WriteAsync(
        string catalogPath,
        string document,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(document);

        var picturePath = RelationshipPicturePath.For(catalogPath);
        var temporary = picturePath + TemporarySuffix;

        try
        {
            await File.WriteAllTextAsync(temporary, document, NoBomUtf8, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, picturePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        return picturePath;
    }

    /// <summary>Best-effort removal of the temporary file; never masks the failure that is on its way out.</summary>
    /// <param name="path">The temporary file the write left behind.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A temporary file that cannot be removed is not worth a second failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same here: the original write failure is the one the log has to carry.
        }
    }
}
