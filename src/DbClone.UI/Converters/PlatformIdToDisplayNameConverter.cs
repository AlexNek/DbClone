using System.Globalization;
using System.Windows.Data;

using DbClone.Application.Platforms;

namespace DbClone.UI.Converters;

/// <summary>
/// Converts a platform stable id (e.g. "supabase") to its display name (e.g. "Supabase")
/// by looking up the platform list provided as the second binding value.
/// Falls back to the raw id when no match is found.
/// Usage: MultiBinding with [0] = platform id, [1] = IReadOnlyList&lt;PlatformEntry&gt;.
/// </summary>
public sealed class PlatformIdToDisplayNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var id = values.Length > 0 ? values[0] as string : null;
        var platforms = values.Length > 1 ? values[1] as IReadOnlyList<PlatformEntry> : null;

        if (id is null || platforms is null)
            return id ?? string.Empty;

        var match = platforms.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        return match?.DisplayName ?? id;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
