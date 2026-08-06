using System.Windows;

using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using Microsoft.Win32;

namespace DbClone.UI.Views;

public partial class ExportConnectionDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ExportConnectionViewModel _viewModel;

    public ExportConnectionDialog(ExportConnectionViewModel viewModel)
    {
        // Assign before InitializeComponent: the "Copy to Clipboard" RadioButton is
        // IsChecked=True, so its Checked event fires during InitializeComponent and
        // the handlers dereference _viewModel.
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
                         {
                             Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                             DefaultExt = ".txt",
                             FileName = "connection.txt"
                         };

        if (dialog.ShowDialog() == true)
            _viewModel.FilePath = dialog.FileName;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClipboardRadioChecked(object sender, RoutedEventArgs e)
    {
        _viewModel.OutputMode = EExportOutputMode.Clipboard;
        if (FilePathPanel != null)
            FilePathPanel.Visibility = Visibility.Collapsed;
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ExportCommand.Execute(null);
        if (_viewModel.Confirmed)
        {
            DialogResult = true;
            Close();
        }
    }

    private void FileRadioChecked(object sender, RoutedEventArgs e)
    {
        _viewModel.OutputMode = EExportOutputMode.File;
        if (FilePathPanel != null)
            FilePathPanel.Visibility = Visibility.Visible;
    }
}
