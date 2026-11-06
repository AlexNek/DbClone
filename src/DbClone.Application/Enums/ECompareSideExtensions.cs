using CanonicalModel.Enums;

namespace DbClone.Application.Enums;

/// <summary>
/// Extensions for <see cref="ECompareSide"/>.
/// </summary>
public static class ECompareSideExtensions
{
    public static string ToDisplayText(this ECompareSide side) =>
        side.ToString().ToLowerInvariant();
}
