using System.Globalization;
using System.IO;
using System.Net;

namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The two conversions every region of the document needs: catalog text into text that can only ever
/// be text, and a coordinate into the shortest string that reads back as the same number.
/// </summary>
/// <remarks>
/// There is no markup builder here on purpose. A region that wants an element writes that element,
/// and reaches for this class only for the value it puts inside — which is what keeps escaping a
/// single decision instead of one per region.
/// </remarks>
internal static class GraphHtmlText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Encodes catalog text for the document body or an attribute. Table names, column names,
    /// constraint names and comments are untrusted input: they came from another database.
    /// </summary>
    /// <param name="value">The text, or <see langword="null"/> for nothing.</param>
    /// <returns>Text no browser can read as markup.</returns>
    internal static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>Formats a coordinate without a culture and without trailing zeros.</summary>
    /// <param name="value">The coordinate.</param>
    /// <returns>The number as a stylesheet and an SVG reader accept it.</returns>
    internal static string Num(double value) => value.ToString("0.###", Invariant);

    /// <summary>
    /// The last segment of a folder path, so the document names the catalog rather than printing a
    /// drive letter in its title.
    /// </summary>
    /// <param name="path">A folder path, with or without a trailing separator.</param>
    /// <returns>The folder's own name, or the path itself when it has none.</returns>
    internal static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
