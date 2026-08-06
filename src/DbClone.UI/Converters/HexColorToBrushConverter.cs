using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DbClone.UI.Converters;

/// <summary>
/// Converts a hex colour string (e.g. "#4CAF50") to a SolidColorBrush.
/// Returns a transparent brush for null, empty, or invalid values.
/// </summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class HexColorToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                // Auto-prepend '#' if the user typed a raw hex value like "4CAF50"
                var normalized = hex.Trim();
                if (normalized.Length is 6 or 8 && !normalized.StartsWith('#'))
                    normalized = "#" + normalized;

                var color = (Color)ColorConverter.ConvertFromString(normalized);
                return new SolidColorBrush(color);
            }
            catch
            {
                // Invalid hex — fall through to transparent
            }
        }

        return TransparentBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
