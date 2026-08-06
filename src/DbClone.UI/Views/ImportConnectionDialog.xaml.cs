using System.Windows;

using DbClone.UI.ViewModels;

namespace DbClone.UI.Views;

public partial class ImportConnectionDialog : Wpf.Ui.Controls.FluentWindow
{
    public ImportConnectionDialog(ImportConnectionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ImportConnectionViewModel vm)
            vm.ImportCommand.Execute(null);

        DialogResult = true;
        Close();
    }
}
