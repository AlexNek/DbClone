namespace DbClone.UI.Services;

/// <summary>
/// Abstraction for the UI dialogs that stay in the presentation layer.
/// The confirmations a workflow needs are declared on
/// <see cref="DbClone.Application.Interfaces.IConfirmationService"/>.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a save file dialog.
    /// </summary>
    /// <param name="filter">File filter (e.g., "HTML File|*.html").</param>
    /// <param name="defaultFileName">Default file name.</param>
    /// <returns>Selected file path, or null if cancelled.</returns>
    string? SaveFile(string filter, string defaultFileName);

    /// <summary>
    /// Shows a folder browser dialog for selecting a directory.
    /// </summary>
    /// <param name="description">Dialog title/description.</param>
    /// <param name="initialDirectory">Initial directory to show.</param>
    /// <returns>Selected folder path, or null if cancelled.</returns>
    string? BrowseFolder(string description, string? initialDirectory = null);
}
