using CanonicalModel.Platforms;

namespace CanonicalModel.Platforms;

/// <summary>
/// Lightweight (id, displayName) pair for populating UI dropdowns.
/// The <paramref name="Id"/> is the stable persistence key; <paramref name="DisplayName"/> is the localizable label.
/// </summary>
public sealed record PlatformEntry(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
