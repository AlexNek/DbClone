namespace DbClone.Application.Interfaces;

/// <summary>
/// The confirmations a workflow needs from the user, expressed without any UI type.
/// </summary>
public interface IConfirmationService
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
}
