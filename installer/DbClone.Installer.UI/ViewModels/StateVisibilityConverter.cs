using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DbClone.Installer.ViewModels;

/// <summary>
/// Collapses an element while the wizard is on any of the states listed in
/// ConverterParameter (comma separated), e.g. "Maintenance,Progress,Failed".
/// Used for the bottom Back/Next buttons, which only make sense on the
/// page-driven wizard steps.
/// </summary>
public sealed class StateVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is InstallerViewModel.InstallerState state && parameter is string list)
        {
            foreach (var name in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse<InstallerViewModel.InstallerState>(name, out var hidden) && hidden == state)
                    return Visibility.Collapsed;
            }
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
