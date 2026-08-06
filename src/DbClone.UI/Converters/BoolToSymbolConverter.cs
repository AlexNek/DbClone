using System.Globalization;
using System.Windows.Data;

using Wpf.Ui.Controls;

namespace DbClone.UI;

/// <summary>
/// Converts a boolean to a <see cref="SymbolRegular"/> glyph value.
/// Used for chevrons and other togglable icons in the UI.
/// </summary>
[ValueConversion(typeof(bool), typeof(SymbolRegular))]
public sealed class BoolToSymbolConverter : IValueConverter
{
    /// <summary>Gets or sets the symbol to return when the value is false.</summary>
    public SymbolRegular FalseValue { get; set; } = SymbolRegular.ChevronRight24;

    /// <summary>Gets or sets the symbol to return when the value is true.</summary>
    public SymbolRegular TrueValue { get; set; } = SymbolRegular.ChevronDown24;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? TrueValue : FalseValue;

        return FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
