using System.IO;
using System.Text;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// The one file a document may ever be written to when it has to leave the app's memory (D10): one
/// fixed name under the user's local folder, overwritten on every use and never suffixed with an
/// origin, a role or a timestamp. A machine that never needs a file holds none.
/// </summary>
/// <remarks>
/// A view is a string (D1). This class exists only for the two cases that cannot stay in memory — the
/// shell opener when the in-app viewer is unavailable, and "open in browser"/"save as" once it is
/// available — so "give me a file" has exactly one path rule in the product.
/// </remarks>
internal static class RelationshipViewFile
{
    /// <summary>The name every written document carries; never derived from what it describes.</summary>
    private const string FileName = "relationship.html";

    /// <summary>UTF-8 without a byte-order mark, the encoding the product's own writers use.</summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Gets the one path a document is written to. Named <c>Path</c> on purpose, so no caller can
    /// believe a second rule exists; inside this class the framework's <see cref="System.IO.Path"/> is
    /// therefore always qualified.
    /// </summary>
    internal static string Path => System.IO.Path.Combine(Folder, FileName);

    private static string Folder =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolder.Name,
            "view");

    /// <summary>Writes the whole document over whatever the previous one left, creating the folder once.</summary>
    /// <param name="html">The document as text.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task WriteAsync(string html, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(html);

        Directory.CreateDirectory(Folder);
        await WriteToAsync(Path, html, ct).ConfigureAwait(false);
    }

    /// <summary>Writes the document wherever a reader asked for it, in the one encoding the product uses.</summary>
    /// <param name="path">Where the reader wants the document.</param>
    /// <param name="html">The document as text.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task WriteToAsync(string path, string html, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(html);

        await File.WriteAllTextAsync(path, html, NoBomUtf8, ct).ConfigureAwait(false);
    }

    /// <summary>Removes the file if one exists; never masks the work that asked for the cleanup.</summary>
    internal static void TryDelete()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A file that could not be removed is not worth a failure at start-up or shut-down.
        }
        catch (UnauthorizedAccessException)
        {
            // Same here: the session that wrote it is over either way.
        }
    }
}
