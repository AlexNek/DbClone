using System.Globalization;
using System.Windows.Data;

namespace DbClone.UI.Converters;

/// <summary>
/// Converts a string to a boolean: true when the string is non-empty, false when empty or null.
/// </summary>
[ValueConversion(typeof(string), typeof(bool))]
public sealed class StringNotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrEmpty(s);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
