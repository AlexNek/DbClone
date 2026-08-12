using DbClone.UI.Models;

namespace DbClone.UI.Services;

/// <summary>
/// Abstraction for UI dialogs, allowing MVVM-friendly confirmation prompts.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a Yes/No confirmation dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog message.</param>
    /// <returns>True if the user clicked Yes, false otherwise.</returns>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>
    /// Shows a three-choice dialog asking how a populated destination should be
    /// cleaned when a table selection is active.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog message.</param>
    /// <returns>The user's choice; defaults to Cancel when the dialog is closed.</returns>
    Task<ESelectionCleanChoice> ConfirmSelectionCleanAsync(string title, string message);

    /// <summary>
    /// Shows a save file dialog.
    /// </summary>
    /// <param name="filter">File filter (e.g., "HTML File|*.html").</param>
    /// <param name="defaultFileName">Default file name.</param>
    /// <returns>Selected file path, or null if cancelled.</returns>
    string? SaveFile(string filter, string defaultFileName);
}
