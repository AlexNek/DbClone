using DbClone.Application.Enums;

namespace DbClone.UI;

/// <summary>
/// Presentation-layer extensions for <see cref="ECompareSide"/>.
/// Internal to this assembly — localizable independently.
/// </summary>
internal static class ECompareSideExtensions
{
    internal static string ToDisplayText(this ECompareSide side) => side.ToString().ToLowerInvariant();
}
