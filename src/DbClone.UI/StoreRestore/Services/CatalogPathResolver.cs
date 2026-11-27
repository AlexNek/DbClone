using System.IO;
using System.Text;

using DbClone.UI.Services;
using DbClone.UI.Settings;

namespace DbClone.UI.StoreRestore.Services;

/// <summary>
/// Resolves catalog paths from user input.
/// Single responsibility: path resolution logic.
/// </summary>
public interface ICatalogPathResolver
{
    string ResolveCatalogPath(string baseDirectory, string pattern, CatalogNameSource names);
    string? PromptForStoreCatalogPath(IDialogService dialogService, UserSettings settings, CatalogNameSource names);
    string? PromptForRestoreCatalogPath(IDialogService dialogService, UserSettings settings);
    string? PromptForOutputPath(IDialogService dialogService, UserSettings settings, string title);
}

/// <summary>
/// Default implementation of <see cref="ICatalogPathResolver"/>.
/// </summary>
public sealed class CatalogPathResolver : ICatalogPathResolver
{
    /// <summary>Longest value one substituted token may contribute, so a long name cannot push the timestamp out.</summary>
    private const int TokenBudget = 60;

    /// <summary>Hard ceiling for the assembled folder name.</summary>
    private const int FolderBudget = 100;

    /// <summary>Names Windows reserves as devices; a folder carrying one cannot be opened by the shell.</summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "CLOCK$", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public string ResolveCatalogPath(string baseDirectory, string pattern, CatalogNameSource names)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return baseDirectory;

        var folder = pattern
            .Replace("{Database}", FitToken(names.DatabaseName), StringComparison.OrdinalIgnoreCase)
            .Replace("{Connection}", FitToken(names.ConnectionToken), StringComparison.OrdinalIgnoreCase)
            .Replace("{timestamp}", DateTime.Now.ToString("yyyy-MM-dd_HH-mm"), StringComparison.OrdinalIgnoreCase);

        folder = CollapseWhitespace(folder);
        folder = ReplaceInvalidChars(folder);
        folder = folder.Trim('.', '_');
        folder = FitFolderName(folder);
        folder = DefuseReservedDeviceName(folder);

        return string.IsNullOrWhiteSpace(folder) ? baseDirectory : Path.Combine(baseDirectory, folder.Trim());
    }

    public string? PromptForStoreCatalogPath(IDialogService dialogService, UserSettings settings, CatalogNameSource names)
    {
        var selectedDirectory = dialogService.BrowseFolder("Select Catalog Directory", settings.StoreRestore.DefaultCatalogDirectory);
        if (string.IsNullOrWhiteSpace(selectedDirectory)) return null;

        var catalogPath = ResolveCatalogPath(selectedDirectory, settings.StoreRestore.CatalogSubfolderPattern, names);
        settings.StoreRestore.DefaultCatalogDirectory = selectedDirectory;
        return catalogPath;
    }

    public string? PromptForRestoreCatalogPath(IDialogService dialogService, UserSettings settings)
    {
        return dialogService.BrowseFolder("Select Catalog Directory", settings.StoreRestore.DefaultCatalogDirectory);
    }

    public string? PromptForOutputPath(IDialogService dialogService, UserSettings settings, string title)
    {
        return dialogService.BrowseFolder(title, settings.StoreRestore.DefaultCatalogDirectory);
    }

    // ── Folder name rules, applied in this order to the assembled name ──────────────

    /// <summary>
    /// Rule 4 at token level: a substituted value never contributes more than the token budget.
    /// The trim belongs to the cut, so a value that fits keeps its own edge characters — inside the
    /// assembled name they are not edges, and rule 3 trims them there if it should.
    /// </summary>
    private static string FitToken(string value) =>
        value.Length <= TokenBudget ? value : Cut(value, TokenBudget).Trim('.', '_');

    /// <summary>
    /// Rule 4 at folder level: the assembled name carries a hard ceiling. A cut can land inside a run
    /// of dots or underscores, so the cut end is trimmed; the other end is the device-name rule's job.
    /// </summary>
    private static string FitFolderName(string value) => Cut(value, FolderBudget).TrimEnd('.', '_');

    /// <summary>
    /// Cuts to <paramref name="budget"/> characters at a character boundary: a high surrogate left as
    /// the last retained character is dropped rather than split from the low surrogate that follows it.
    /// </summary>
    private static string Cut(string value, int budget)
    {
        if (value.Length <= budget) return value;

        var length = budget;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    /// <summary>Rule 1: every run of whitespace collapses into a single underscore.</summary>
    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace) builder.Append('_');
                previousWasWhitespace = true;
                continue;
            }

            previousWasWhitespace = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Rule 2: every character a Windows file name cannot hold becomes an underscore.</summary>
    private static string ReplaceInvalidChars(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return value;
    }

    /// <summary>
    /// Rule 5: a name Windows reserves as a device, with or without an extension, gets an underscore
    /// prefix so the folder it names stays creatable and shell-openable. It runs after the length cut,
    /// so a name the cut reduced to a device name is defused as well.
    /// </summary>
    private static string DefuseReservedDeviceName(string value)
    {
        var dot = value.IndexOf('.');
        var stem = dot < 0 ? value : value[..dot];

        return ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)
            ? "_" + value
            : value;
    }
}
