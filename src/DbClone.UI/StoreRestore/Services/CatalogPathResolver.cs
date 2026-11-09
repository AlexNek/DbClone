using System.IO;

using DbClone.UI.Services;
using DbClone.UI.Settings;

namespace DbClone.UI.StoreRestore.Services;

/// <summary>
/// Resolves catalog paths from user input.
/// Single responsibility: path resolution logic.
/// </summary>
public interface ICatalogPathResolver
{
    string ResolveCatalogPath(string baseDirectory, string pattern, string databaseName);
    string? PromptForStoreCatalogPath(IDialogService dialogService, UserSettings settings, string databaseName);
    string? PromptForRestoreCatalogPath(IDialogService dialogService, UserSettings settings);
    string? PromptForOutputPath(IDialogService dialogService, UserSettings settings, string title);
}

/// <summary>
/// Default implementation of <see cref="ICatalogPathResolver"/>.
/// </summary>
public sealed class CatalogPathResolver : ICatalogPathResolver
{
    public string ResolveCatalogPath(string baseDirectory, string pattern, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return baseDirectory;

        var folder = pattern
            .Replace("{Database}", databaseName, StringComparison.OrdinalIgnoreCase)
            .Replace("{timestamp}", DateTime.Now.ToString("yyyy-MM-dd_HH-mm"), StringComparison.OrdinalIgnoreCase);

        foreach (var invalid in Path.GetInvalidFileNameChars())
            folder = folder.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(folder) ? baseDirectory : Path.Combine(baseDirectory, folder.Trim());
    }

    public string? PromptForStoreCatalogPath(IDialogService dialogService, UserSettings settings, string databaseName)
    {
        var selectedDirectory = dialogService.BrowseFolder("Select Catalog Directory", settings.StoreRestore.DefaultCatalogDirectory);
        if (string.IsNullOrWhiteSpace(selectedDirectory)) return null;

        var catalogPath = ResolveCatalogPath(selectedDirectory, settings.StoreRestore.CatalogSubfolderPattern, databaseName);
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
}
