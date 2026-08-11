using System.Windows;

using DbClone.UI.Models;
using DbClone.UI.Views;

using Microsoft.Win32;

namespace DbClone.UI.Services;

/// <summary>
/// WPF implementation of <see cref="IDialogService"/> using MessageBox.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message)
    {
        var result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    /// <inheritdoc />
    public Task<ESelectionCleanChoice> ConfirmSelectionCleanAsync(string title, string message)
    {
        var dialog = new CleanTargetDialog
                         {
                             DataContext = new { Title = title, Message = message },
                             Owner = System.Windows.Application.Current.MainWindow
                         };

        dialog.ShowDialog();

        return Task.FromResult(dialog.Choice);
    }

    /// <inheritdoc />
    public string? SaveFile(string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
                         {
                             Filter = filter, FileName = defaultFileName, Title = "Export Report"
                         };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
