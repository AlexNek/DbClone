using CanonicalModel.Platforms;

namespace DbAdapter.Definitions;

/// <summary>
/// The platform role: which hosted variant of the engine a connection is, and what it brings.
/// Stateless — one instance per engine.
/// </summary>
public interface IPlatformResolver
{
    /// <summary>System and platform schemas that apply to <paramref name="host"/> on the given server version.</summary>
    PlatformResolution Resolve(string engine, string host, string serverVersion);

    /// <summary>Stable id of the hosted variant a host belongs to, or null for the plain engine.</summary>
    string? DetectPlatformId(string engine, string host);

    /// <summary>Connection defaults (port, TLS mode) of a hosted variant.</summary>
    PlatformDefaults GetConnectionDefaults(string engine, string? platformId);

    /// <summary>Every hosted variant the engine declares, plain engine included.</summary>
    IReadOnlyList<PlatformEntry> GetAllPlatforms(string engine);
}
