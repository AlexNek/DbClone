using System.Globalization;
using System.Windows.Data;

using DbClone.UI.ViewModels;

using Wpf.Ui.Controls;

namespace DbClone.UI;

/// <summary>
/// Converts an <see cref="EObjectStatus"/> to a <see cref="SymbolRegular"/> glyph.
/// </summary>
[ValueConversion(typeof(EObjectStatus), typeof(SymbolRegular))]
public sealed class ObjectStatusToSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is EObjectStatus status)
        {
            return status switch
                {
                    EObjectStatus.Done => SymbolRegular.Checkmark24,
                    EObjectStatus.InProgress => SymbolRegular.ArrowSync24,
                    EObjectStatus.Failed => SymbolRegular.DismissCircle24,
                    _ => SymbolRegular.Circle24
                };
        }

        return SymbolRegular.Circle24;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
