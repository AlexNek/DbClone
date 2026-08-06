using System.Windows;

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
    public string? SaveFile(string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
                         {
                             Filter = filter, FileName = defaultFileName, Title = "Export Report"
                         };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
