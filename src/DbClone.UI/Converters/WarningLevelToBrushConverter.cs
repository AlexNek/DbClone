using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

using DbClone.Application.Enums;

namespace DbClone.UI.Converters;

/// <summary>
/// Converts <see cref="EWarningLevel"/> to a <see cref="Brush"/> for visual severity indication.
/// </summary>
[ValueConversion(typeof(EWarningLevel), typeof(Brush))]
public sealed class WarningLevelToBrushConverter : IValueConverter
{
    private static readonly Brush
        ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)); // red

    private static readonly Brush
        InfoBrush = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61)); // gray

    private static readonly Brush WarningBrush =
        new SolidColorBrush(Color.FromRgb(0xF5, 0x7C, 0x00)); // amber

    static WarningLevelToBrushConverter()
    {
        ErrorBrush.Freeze();
        WarningBrush.Freeze();
        InfoBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
            {
                EWarningLevel.Error => ErrorBrush,
                EWarningLevel.Warning => WarningBrush,
                _ => InfoBrush
            };

    public object
        ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
