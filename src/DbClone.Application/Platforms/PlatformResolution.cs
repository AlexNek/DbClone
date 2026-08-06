using System.Collections.Immutable;

namespace DbClone.Application.Platforms;

/// <summary>
/// Result of resolving platform definitions for a specific connection.
/// All sets are immutable and case-insensitive.
/// </summary>
/// <param name="DetectedPlatform">
/// Display name of the detected platform (e.g. "Supabase"), or null if using
/// the base engine file (vanilla PostgreSQL).
/// </param>
/// <param name="SystemSchemas">
/// Engine-internal schemas — always excluded from content reads, presence-checked
/// on both sides during comparison, and repaired on destination during copy.
/// </param>
/// <param name="PlatformSchemas">
/// Provider-managed schemas — excluded only when the user setting
/// (CopyPlatformSchemas / ComparePlatformSchemas) is unchecked.
/// </param>
/// <param name="PlatformExtensions">Provider-managed extensions.</param>
/// <param name="VersionWarning">
/// Non-null when the connected server version has no matching entry in the
/// platform file. Contains a user-facing message indicating which file to update.
/// </param>
public sealed record PlatformResolution(
    string? DetectedPlatform,
    IReadOnlySet<string> SystemSchemas,
    IReadOnlySet<string> PlatformSchemas,
    IReadOnlySet<string> PlatformExtensions,
    string? VersionWarning = null)
{
    /// <summary>
    /// Empty resolution — used when no platform files are loaded at all.
    /// </summary>
    public static readonly PlatformResolution None = new(
        null,
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty);
}
