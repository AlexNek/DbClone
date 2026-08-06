using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DbClone.UI.Converters;

/// <summary>
/// Converts an integer to Visibility: Visible when value &gt; 0, Collapsed otherwise.
/// </summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class GreaterThanZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object
        ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
