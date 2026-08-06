using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DbClone.UI.Converters;

/// <summary>
/// Converts between an enum value and a boolean for RadioButton binding.
/// Use ConverterParameter to specify the enum value this RadioButton represents.
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null)
        {
            if (targetType.IsEnum)
                return Enum.Parse(targetType, parameter.ToString()!);
        }

        return DependencyProperty.UnsetValue;
    }
}
