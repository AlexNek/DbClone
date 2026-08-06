using System.Windows;

using DbClone.UI.ViewModels;

namespace DbClone.UI.Views;

/// <summary>
/// Thin dialog shell for the colour picker. All logic lives in <see cref="ColorPickerViewModel"/>.
/// The <see cref="ColorPickerControl.SelectedColor"/> DP is bound TwoWay to the VM's
/// <c>SelectedColor</c>, so the VM is always in sync with the swatch selection.
/// </summary>
public partial class ColorPickerDialog : Window
{
    public ColorPickerDialog(ColorPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OkClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
