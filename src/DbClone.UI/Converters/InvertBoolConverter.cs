using System.Globalization;
using System.Windows.Data;

namespace DbClone.UI.Converters;

/// <summary>
/// Inverts a boolean value. Used for IsEnabled bindings that should be true when a source bool is false.
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}
